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
    public SlotStatus SlotStatus => _journal.SlotStatus;
    public bool IsActiveInterruptedBySessionClose =>
        _journal.SlotStatus == SlotStatus.ACTIVE && _journal.ExecutionInterruptedByClose && !_journal.Committed;
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

    public void UpdateTradingDate(DateOnly newTradingDate, DateTimeOffset utcNow)
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
                if (State != StreamState.PRE_HYDRATION)
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
                ReentrySubmitted = previousJournal.ReentrySubmitted,
                ReentryFilled = previousJournal.ReentryFilled,
                ProtectionSubmitted = previousJournal.ProtectionSubmitted,
                ProtectionAccepted = previousJournal.ProtectionAccepted,
                NextSlotTimeUtc = previousJournal.NextSlotTimeUtc,
                PriorJournalKey = $"{previousTradingDateStr}_{Stream}" // Reference to previous day's journal
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
    /// Handle PRE_HYDRATION state logic.
    /// </summary>
    private void HandlePreHydrationState(DateTimeOffset utcNow)
    {
        // DIAGNOSTIC: Rate-limited DEBUG log to confirm PRE_HYDRATION handler is entered
        // Log once per stream per 5 minutes
        // CRITICAL: Always log (not gated by enable_diagnostic_logs) - needed for debugging stuck streams
        var shouldLogHandlerTrace = !_lastPreHydrationHandlerTraceUtc.HasValue || 
            (utcNow - _lastPreHydrationHandlerTraceUtc.Value).TotalMinutes >= 5.0;
        
        if (shouldLogHandlerTrace && _log != null && _time != null)
        {
            try
            {
                _lastPreHydrationHandlerTraceUtc = utcNow;
                var nowChicago = _time.ConvertUtcToChicago(utcNow);
                int barCount = GetBarBufferCount();
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "PRE_HYDRATION_HANDLER_TRACE", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        state = State.ToString(),
                        now_chicago = nowChicago.ToString("o"),
                        bar_count = barCount,
                        note = "Diagnostic: Confirms HandlePreHydrationState() is executing"
                    }));
            }
            catch (Exception ex)
            {
                // Log error but don't throw
                try
                {
                    if (_log != null)
                    {
                        _log.Write(RobotEvents.Base(_time ?? new TimeService("America/Chicago"), utcNow, TradingDate ?? "", Stream ?? "", Instrument ?? "", Session ?? "", SlotTimeChicago ?? "", SlotTimeUtc,
                            "PRE_HYDRATION_HANDLER_TRACE_ERROR", State.ToString(),
                            new
                            {
                                stream_id = Stream ?? "N/A",
                                error = ex.Message,
                                note = "Diagnostic: HandlePreHydrationState() called but logging failed"
                            }));
                    }
                }
                catch (Exception innerEx)
                {
                    LogCriticalError("PRE_HYDRATION_HANDLER_TRACE_ERROR_FALLBACK_FAILED", innerEx, utcNow, new
                    {
                        original_error = ex.Message,
                        note = "Failed to log PRE_HYDRATION_HANDLER_TRACE_ERROR fallback"
                    });
                }
            }
        }

        // DRYRUN mode: Use file-based pre-hydration only
        // SIM mode: Use NinjaTrader BarsRequest only (no CSV files)
        if (!_preHydrationComplete)
        {
            if (IsSimMode())
            {
                // SIM mode: Skip CSV files, rely solely on BarsRequest
                // CRITICAL FIX: Wait for BarsRequest to complete before marking pre-hydration complete
                // This prevents range lock from happening before historical bars arrive
                // CRITICAL: Check both CanonicalInstrument and ExecutionInstrument
                // BarsRequest might be marked pending with either one
                var isPending = _engine != null && (
                    _engine.IsBarsRequestPending(CanonicalInstrument, utcNow) ||
                    _engine.IsBarsRequestPending(ExecutionInstrument, utcNow)
                );
                
                if (isPending)
                {
                    // BarsRequest is still pending - wait for it to complete
                    // Log diagnostic to show we're waiting
                    var shouldLogWait = !_lastPreHydrationHandlerTraceUtc.HasValue || 
                        (utcNow - _lastPreHydrationHandlerTraceUtc.Value).TotalMinutes >= 1.0;
                    
                    if (shouldLogWait && _log != null && _time != null)
                    {
                        try
                        {
                            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                                "PRE_HYDRATION_WAITING_FOR_BARSREQUEST", State.ToString(),
                                new
                                {
                                    stream_id = Stream,
                                    canonical_instrument = CanonicalInstrument,
                                    execution_instrument = ExecutionInstrument,
                                    execution_mode = _executionMode.ToString(),
                                    note = "Waiting for BarsRequest to complete before marking pre-hydration complete"
                                }));
                        }
                        catch { }
                    }
                    return; // Stay in PRE_HYDRATION state until BarsRequest completes
                }
                
                // BarsRequest is not pending (either completed or never requested)
                // Mark pre-hydration as complete so we can transition when bars arrive
                _preHydrationComplete = true;
                
                // DIAGNOSTIC: Log when _preHydrationComplete is set to true in SIM mode
                if (_log != null && _time != null)
                {
                    try
                    {
                        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "PRE_HYDRATION_COMPLETE_SET", State.ToString(),
                            new
                            {
                                stream_id = Stream,
                                execution_mode = _executionMode.ToString(),
                                bars_request_pending = _engine != null && (
                                    _engine.IsBarsRequestPending(CanonicalInstrument, utcNow) ||
                                    _engine.IsBarsRequestPending(ExecutionInstrument, utcNow)
                                ),
                                note = "Diagnostic: _preHydrationComplete set to true in SIM mode (BarsRequest not pending)"
                            }));
                    }
                    catch { }
                }
            }
            else
            {
                // DRYRUN mode: Perform file-based pre-hydration
                PerformPreHydration(utcNow);
            }
        }
        
        // After pre-hydration setup completes, check if we should transition to ARMED
        if (_preHydrationComplete)
        {
            // DIAGNOSTIC: Log that we entered the _preHydrationComplete block
            // This confirms the block is being entered
            try
            {
                if (_log != null)
                {
                    _log.Write(RobotEvents.Base(_time ?? new TimeService("America/Chicago"), utcNow, TradingDate ?? "", Stream ?? "", Instrument ?? "", Session ?? "", SlotTimeChicago ?? "", SlotTimeUtc,
                        "PRE_HYDRATION_COMPLETE_BLOCK_ENTERED", State.ToString(),
                        new
                        {
                            stream_id = Stream ?? "N/A",
                            log_null = _log == null,
                            time_null = _time == null,
                            note = "Diagnostic: Entered _preHydrationComplete block"
                        }));
                }
            }
            catch (Exception ex)
            {
                LogCriticalError("PRE_HYDRATION_COMPLETE_BLOCK_ENTERED_ERROR", ex, utcNow, new
                {
                    note = "Failed to log PRE_HYDRATION_COMPLETE_BLOCK_ENTERED"
                });
            }
            
            // DIAGNOSTIC: Log immediately after COMPLETE_BLOCK_ENTERED to verify execution continues
            try
            {
                if (_log != null)
                {
                    _log.Write(RobotEvents.Base(_time ?? new TimeService("America/Chicago"), utcNow, TradingDate ?? "", Stream ?? "", Instrument ?? "", Session ?? "", SlotTimeChicago ?? "", SlotTimeUtc,
                        "PRE_HYDRATION_AFTER_COMPLETE_BLOCK", State.ToString(),
                        new
                        {
                            stream_id = Stream ?? "N/A",
                            note = "Diagnostic: Execution continues after PRE_HYDRATION_COMPLETE_BLOCK_ENTERED"
                        }));
                }
            }
            catch (Exception ex)
            {
                LogCriticalError("PRE_HYDRATION_AFTER_COMPLETE_BLOCK_ERROR", ex, utcNow, new
                {
                    note = "Failed to log PRE_HYDRATION_AFTER_COMPLETE_BLOCK"
                });
            }
            
            // Common variables for all execution modes
            int barCount = GetBarBufferCount();
            var nowChicago = _time != null ? _time.ConvertUtcToChicago(utcNow) : default(DateTimeOffset);
            
            // DIAGNOSTIC: Log after GetBarBufferCount and ConvertUtcToChicago to verify they executed
            try
            {
                if (_log != null)
                {
                    _log.Write(RobotEvents.Base(_time ?? new TimeService("America/Chicago"), utcNow, TradingDate ?? "", Stream ?? "", Instrument ?? "", Session ?? "", SlotTimeChicago ?? "", SlotTimeUtc,
                        "PRE_HYDRATION_AFTER_VARIABLES", State.ToString(),
                        new
                        {
                            stream_id = Stream ?? "N/A",
                            bar_count = barCount,
                            note = "Diagnostic: After GetBarBufferCount and ConvertUtcToChicago"
                        }));
                }
            }
            catch { }
            
            // DIAGNOSTIC: Log RangeStartChicagoTime at the point of use (before any guards)
            // This confirms whether the timeout is being skipped because the value is unset
            // CRITICAL: Always log (not gated by enable_diagnostic_logs) - needed for debugging timeout issues
            
            // DIAGNOSTIC: Log before the null check to verify we reach this point
            try
            {
                if (_log != null)
                {
                    _log.Write(RobotEvents.Base(_time ?? new TimeService("America/Chicago"), utcNow, TradingDate ?? "", Stream ?? "", Instrument ?? "", Session ?? "", SlotTimeChicago ?? "", SlotTimeUtc,
                        "PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC", State.ToString(),
                        new
                        {
                            stream_id = Stream ?? "N/A",
                            log_null = _log == null,
                            time_null = _time == null,
                            note = "Diagnostic: About to check for PRE_HYDRATION_RANGE_START_DIAGNOSTIC"
                        }));
                }
            }
            catch (Exception ex)
            {
                LogCriticalError("PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC_ERROR", ex, utcNow, new
                {
                    note = "Failed to log PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC"
                });
            }
            
            if (_log != null && _time != null) // Always log this critical diagnostic, but check for nulls
            {
                try
                {
                    var rangeStartIsDefault = RangeStartChicagoTime == default(DateTimeOffset);
                    var rangeStartDate = rangeStartIsDefault ? "N/A" : RangeStartChicagoTime.Date.ToString("yyyy-MM-dd");
                    var expectedDate1 = TradingDate;
                    var expectedDate2 = TimeService.TryParseDateOnly(TradingDate, out var tradingDateOnly) 
                        ? tradingDateOnly.AddDays(-1).ToString("yyyy-MM-dd") 
                        : "N/A";
                    
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "PRE_HYDRATION_RANGE_START_DIAGNOSTIC", State.ToString(),
                        new
                        {
                            stream_id = Stream,
                            now_chicago = nowChicago.ToString("o"),
                            range_start_chicago_raw = RangeStartChicagoTime.ToString("o"),
                            range_start_is_default = rangeStartIsDefault,
                            range_start_year = RangeStartChicagoTime.Year,
                            range_start_date = rangeStartDate,
                            minutes_until_range_start = Math.Round((RangeStartChicagoTime - nowChicago).TotalMinutes, 2),
                            minutes_past_range_start = Math.Round((nowChicago - RangeStartChicagoTime).TotalMinutes, 2),
                            is_before_range_start = nowChicago < RangeStartChicagoTime,
                            trading_date = TradingDate,
                            expected_valid_dates = new[] { expectedDate1, expectedDate2 },
                            note = "Diagnostic: RangeStartChicagoTime value before timeout guards"
                        }));
                }
                catch (Exception ex)
                {
                    // Log error but don't throw
                    try
                    {
                        if (_log != null)
                        {
                            _log.Write(RobotEvents.Base(_time ?? new TimeService("America/Chicago"), utcNow, TradingDate ?? "", Stream ?? "", Instrument ?? "", Session ?? "", SlotTimeChicago ?? "", SlotTimeUtc,
                                "PRE_HYDRATION_RANGE_START_DIAGNOSTIC_ERROR", State.ToString(),
                                new
                                {
                                    stream_id = Stream ?? "N/A",
                                    error = ex.Message,
                                    exception_type = ex.GetType().Name,
                                    stack_trace = ex.StackTrace != null && ex.StackTrace.Length > 500 ? ex.StackTrace.Substring(0, 500) : ex.StackTrace,
                                    note = "Diagnostic: PRE_HYDRATION_RANGE_START_DIAGNOSTIC logging failed"
                                }));
                        }
                    }
                    catch (Exception innerEx)
                    {
                        LogCriticalError("PRE_HYDRATION_RANGE_START_DIAGNOSTIC_ERROR_FALLBACK_FAILED", innerEx, utcNow, new
                        {
                            original_error = ex.Message,
                            original_exception_type = ex.GetType().Name,
                            note = "Failed to log PRE_HYDRATION_RANGE_START_DIAGNOSTIC_ERROR fallback"
                        });
                    }
                }
            }
            else
            {
                // DIAGNOSTIC: Log if null check fails
                try
                {
                    if (_log != null)
                    {
                        _log.Write(RobotEvents.Base(_time ?? new TimeService("America/Chicago"), utcNow, TradingDate ?? "", Stream ?? "", Instrument ?? "", Session ?? "", SlotTimeChicago ?? "", SlotTimeUtc,
                            "PRE_HYDRATION_RANGE_START_DIAGNOSTIC_NULL_CHECK_FAILED", State.ToString(),
                            new
                            {
                                stream_id = Stream ?? "N/A",
                                log_null = _log == null,
                                time_null = _time == null,
                                note = "Diagnostic: Null check failed for PRE_HYDRATION_RANGE_START_DIAGNOSTIC"
                            }));
                    }
                }
                catch (Exception ex)
                {
                    LogCriticalError("PRE_HYDRATION_RANGE_START_DIAGNOSTIC_NULL_CHECK_FAILED_ERROR", ex, utcNow, new
                    {
                        note = "Failed to log PRE_HYDRATION_RANGE_START_DIAGNOSTIC_NULL_CHECK_FAILED"
                    });
                }
            }
            
            // HARD TIMEOUT: Liveness guarantee - PRE_HYDRATION must exit no later than RangeStartChicagoTime + 1 minute
            // Range-start-relative timeout (not wall-clock fragile)
            // CRITICAL: This timeout applies to ALL execution modes (SIM and DRYRUN)
            var hardTimeoutChicago = RangeStartChicagoTime.AddMinutes(1.0);
            var minutesPastRangeStart = (nowChicago - RangeStartChicagoTime).TotalMinutes;
            var shouldForceTransition = false;
            var forceTransitionReason = "";
            
            // Validate RangeStartChicagoTime before forcing transition
            if (RangeStartChicagoTime != default(DateTimeOffset) && RangeStartChicagoTime.Year > 2000)
            {
                // Trading Date Context Check: RangeStartChicagoTime.Date must match active trading date context
                // RangeStartChicagoTime.Date should be either:
                // - The trading date (if range starts on trading date), OR
                // - The prior session date (if range starts evening before trading date)
                if (TimeService.TryParseDateOnly(TradingDate, out var tradingDateOnly))
                {
                    var rangeStartDate = DateOnly.FromDateTime(RangeStartChicagoTime.Date);
                    var priorSessionDate = tradingDateOnly.AddDays(-1);
                    
                    var dateMatches = rangeStartDate == tradingDateOnly || rangeStartDate == priorSessionDate;
                    
                    if (dateMatches && nowChicago >= hardTimeoutChicago)
                    {
                        shouldForceTransition = true;
                        forceTransitionReason = "HARD_TIMEOUT";
                    }
                    else if (!dateMatches)
                    {
                        // RangeStartChicagoTime date does not match trading date context - may be stale from previous run
                        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "PRE_HYDRATION_TIMEOUT_SKIPPED", State.ToString(),
                            new
                            {
                                range_start_chicago = RangeStartChicagoTime.ToString("o"),
                                range_start_date = RangeStartChicagoTime.Date.ToString("yyyy-MM-dd"),
                                trading_date = TradingDate,
                                reason = "RANGE_START_DATE_MISMATCH",
                                note = "RangeStartChicagoTime date does not match trading date context - may be stale from previous run"
                            }));
                    }
                }
            }
            else
            {
                // RangeStartChicagoTime is invalid (default/zero/uninitialized) - config bug
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "PRE_HYDRATION_TIMEOUT_SKIPPED", State.ToString(),
                    new
                    {
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        range_start_year = RangeStartChicagoTime.Year,
                        trading_date = TradingDate,
                        reason = "RANGE_START_INVALID",
                        note = "RangeStartChicagoTime is invalid (default/zero/uninitialized) - config bug"
                    }));
            }
            
            // WATCHDOG: Log if stream is stuck in PRE_HYDRATION more than 1 minute after range start
            if (minutesPastRangeStart >= 1.0)
            {
                var timeInState = _stateEntryTimeUtc.HasValue 
                    ? (utcNow - _stateEntryTimeUtc.Value).TotalMinutes 
                    : (double?)null;
                
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "PRE_HYDRATION_WATCHDOG_STUCK", State.ToString(),
                    new
                    {
                        bar_count = barCount,
                        now_chicago = nowChicago.ToString("o"),
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        minutes_past_range_start = Math.Round(minutesPastRangeStart, 2),
                        time_in_state_minutes = timeInState.HasValue ? Math.Round(timeInState.Value, 2) : (double?)null,
                        condition_bar_count_gt_zero = barCount > 0,
                        condition_now_ge_range_start = nowChicago >= RangeStartChicagoTime,
                        condition_met = barCount > 0 || nowChicago >= RangeStartChicagoTime,
                        warning = "Stream stuck in PRE_HYDRATION more than 1 minute after range start",
                        note = "Stream should have transitioned to ARMED. Check BarsRequest execution and bar delivery."
                    }));
            }
            
            if (IsSimMode())
            {
                // SIM mode: Wait for BarsRequest bars from NinjaTrader
                // Check if we have sufficient bars or if we're past range start time
                
                // DIAGNOSTIC: Log condition evaluation periodically to debug stuck streams
                var shouldLogCondition = !_lastHeartbeatUtc.HasValue || 
                    (utcNow - _lastHeartbeatUtc.Value).TotalMinutes >= 5.0 ||
                    nowChicago >= RangeStartChicagoTime;
                
                if (shouldLogCondition && _enableDiagnosticLogs)
                {
                    _lastHeartbeatUtc = utcNow;
                    var conditionMet = barCount > 0 || nowChicago >= RangeStartChicagoTime;
                    var timeInState = _stateEntryTimeUtc.HasValue 
                        ? (utcNow - _stateEntryTimeUtc.Value).TotalMinutes 
                        : (double?)null;
                    
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "PRE_HYDRATION_CONDITION_CHECK", State.ToString(),
                        new
                        {
                            bar_count = barCount,
                            now_chicago = nowChicago.ToString("o"),
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            time_in_state_minutes = timeInState.HasValue ? Math.Round(timeInState.Value, 2) : (double?)null,
                            condition_bar_count_gt_zero = barCount > 0,
                            condition_now_ge_range_start = nowChicago >= RangeStartChicagoTime,
                            condition_met = conditionMet,
                            will_transition = conditionMet,
                            note = "Diagnostic: Checking transition condition from PRE_HYDRATION to ARMED"
                        }));
                }
                
                // Transition to ARMED if we have bars, if we're past range start time, or if hard timeout forces it
                if (barCount > 0 || nowChicago >= RangeStartChicagoTime || shouldForceTransition)
                {
                    // Log forced transition if hard timeout triggered
                    if (shouldForceTransition)
                    {
                        // Mark zero-bar hydration if hard timeout forced transition with 0 bars
                        if (barCount == 0)
                        {
                            _hadZeroBarHydration = true;
                        }
                        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "PRE_HYDRATION_FORCED_TRANSITION", State.ToString(),
                            new
                            {
                                reason = forceTransitionReason,
                                trading_date = TradingDate,
                                range_start_chicago = RangeStartChicagoTime.ToString("o"),
                                range_start_date = RangeStartChicagoTime.Date.ToString("yyyy-MM-dd"),
                                hard_timeout_chicago = hardTimeoutChicago.ToString("o"),
                                minutes_past_range_start = Math.Round(minutesPastRangeStart, 2),
                                bar_count = barCount,
                                note = "Liveness guarantee: PRE_HYDRATION forced to ARMED after RangeStartChicagoTime + 1 minute (range-start-relative)"
                            }));
                    }
                    // Log timeout if transitioning without bars (but not forced)
                    else if (barCount == 0 && nowChicago >= RangeStartChicagoTime)
                    {
                        _hadZeroBarHydration = true; // Mark zero-bar hydration (hard timeout with 0 bars)
                        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, 
                            SlotTimeChicago, SlotTimeUtc,
                            "PRE_HYDRATION_TIMEOUT_NO_BARS", "PRE_HYDRATION",
                            new
                            {
                                stream_id = Stream,
                                instrument = Instrument,
                                trading_date = TradingDate,
                                now_chicago = nowChicago.ToString("o"),
                                range_start_chicago = RangeStartChicagoTime.ToString("o"),
                                note = "Proceeding without historical bars"
                            }));
                    }
                    
                    // CONSOLIDATED HYDRATION SUMMARY LOG: Forensic snapshot for every day
                    // This single log captures all hydration statistics at PRE_HYDRATION → ARMED transition
                    // Collect all bar source counters for comprehensive reporting
                    int historicalBarCount, liveBarCount, dedupedBarCount, filteredFutureBarCount, filteredPartialBarCount;
                    lock (_barBufferLock)
                    {
                        historicalBarCount = _historicalBarCount;
                        liveBarCount = _liveBarCount;
                        dedupedBarCount = _dedupedBarCount;
                        filteredFutureBarCount = _filteredFutureBarCount;
                        filteredPartialBarCount = _filteredPartialBarCount;
                    }
                    
                    // LATE-START SAFE HANDLING: Reconstruct range and check for missed breakout
                    // Range build window: [range_start, slot_time) - slot_time is EXCLUSIVE
                    // Missed-breakout scan window: [slot_time, now] - only if late start
                    decimal? reconstructedRangeHigh = null;
                    decimal? reconstructedRangeLow = null;
                    bool missedBreakout = false;
                    DateTimeOffset? breakoutTimeUtc = null;
                    DateTimeOffset? breakoutTimeChicago = null;
                    decimal? breakoutPrice = null;
                    string? breakoutDirection = null;
                    bool isLateStart = nowChicago > SlotTimeChicagoTime;
                    
                    try
                    {
                        // Compute range strictly from bars < slot_time (slot_time exclusive)
                        var rangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: SlotTimeUtc);
                        
                        if (rangeResult.Success && rangeResult.RangeHigh.HasValue && rangeResult.RangeLow.HasValue)
                        {
                            reconstructedRangeHigh = rangeResult.RangeHigh.Value;
                            reconstructedRangeLow = rangeResult.RangeLow.Value;
                            
                            // If starting after slot_time, check if breakout already occurred
                            if (isLateStart)
                            {
                                var missedBreakoutResult = CheckMissedBreakout(utcNow, reconstructedRangeHigh.Value, reconstructedRangeLow.Value);
                                missedBreakout = missedBreakoutResult.MissedBreakout;
                                breakoutTimeUtc = missedBreakoutResult.BreakoutTimeUtc;
                                breakoutTimeChicago = missedBreakoutResult.BreakoutTimeChicago;
                                breakoutPrice = missedBreakoutResult.BreakoutPrice;
                                breakoutDirection = missedBreakoutResult.BreakoutDirection;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Non-blocking: log error but continue
                        LogHealth("WARN", "HYDRATION_RANGE_COMPUTE_ERROR", 
                            $"Range computation or missed-breakout check failed: {ex.Message}. Continuing with normal flow.",
                            new { error = ex.ToString() });
                    }
                    
                    // Calculate completeness metrics (non-blocking)
                    int expectedBars = 0;
                    int expectedFullRangeBars = 0;
                    double completenessPct = 0.0;
                    try
                    {
                        var hydrationEndChicago = nowChicago < SlotTimeChicagoTime ? nowChicago : SlotTimeChicagoTime;
                        var rangeDurationMinutes = (hydrationEndChicago - RangeStartChicagoTime).TotalMinutes;
                        var fullRangeDurationMinutes = (SlotTimeChicagoTime - RangeStartChicagoTime).TotalMinutes;
                        
                        expectedBars = Math.Max(0, (int)Math.Floor(rangeDurationMinutes));
                        expectedFullRangeBars = Math.Max(0, (int)Math.Floor(fullRangeDurationMinutes));
                        
                        if (expectedBars > 0)
                        {
                            completenessPct = Math.Min(100.0, (barCount / (double)expectedBars) * 100.0);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Non-blocking: metrics calculation failed, continue without them
                        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "HYDRATION_COMPLETENESS_CALC_ERROR", State.ToString(),
                            new { error = ex.Message, note = "Completeness calculation failed, continuing without metrics" }));
                    }
                    
                    // DEBUG: Log boundary contract to prevent regressions
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "HYDRATION_BOUNDARY_CONTRACT", State.ToString(),
                        new
                        {
                            range_build_window = $"[{RangeStartChicagoTime:HH:mm:ss}, {SlotTimeChicagoTime:HH:mm:ss})",
                            range_build_window_note = "slot_time is EXCLUSIVE for range building",
                            missed_breakout_scan_window = isLateStart ? $"[{SlotTimeChicagoTime:HH:mm:ss}, {nowChicago:HH:mm:ss}]" : "N/A (not late start)",
                            missed_breakout_scan_note = isLateStart ? "Only checked if now > slot_time" : "Not applicable",
                            note = "Boundary contract for range reconstruction and missed-breakout detection"
                        }));
                    
                    // CRITICAL FIX: Re-read barCount right before logging HYDRATION_SUMMARY
                    // barCount was captured at the start of HandlePreHydrationState(), but bars are added
                    // asynchronously via AddBarToBuffer() from BarsRequest callbacks or live feed.
                    // We must read the current buffer count to get accurate loaded_bars.
                    int currentBarCount = GetBarBufferCount();
                    
                    // Log HYDRATION_SUMMARY with range and missed breakout details (even if missed breakout occurred)
                    var hydrationNote = missedBreakout 
                        ? $"MISSED_BREAKOUT: Starting after slot_time but breakout already occurred at {breakoutTimeChicago?.ToString("HH:mm:ss") ?? "N/A"} CT. Range computed but trading blocked."
                        : "Consolidated hydration summary - forensic snapshot at PRE_HYDRATION → ARMED transition. " +
                          "This log captures all bar sources, filtering, deduplication statistics, completeness metrics, and late-start handling for debugging and auditability.";
                    
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "HYDRATION_SUMMARY", "PRE_HYDRATION",
                        new
                        {
                            stream_id = Stream,
                            canonical_instrument = CanonicalInstrument,
                            instrument = Instrument,
                            slot = Stream,
                            trading_date = TradingDate,
                            total_bars_in_buffer = currentBarCount,
                            // Bar source breakdown
                            historical_bar_count = historicalBarCount,
                            live_bar_count = liveBarCount,
                            deduped_bar_count = dedupedBarCount,
                            filtered_future_bar_count = filteredFutureBarCount,
                            filtered_partial_bar_count = filteredPartialBarCount,
                            // Timing context
                            now_chicago = nowChicago.ToString("o"),
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                            // Completeness metrics
                            expected_bars = expectedBars,
                            expected_full_range_bars = expectedFullRangeBars,
                            loaded_bars = currentBarCount,
                            completeness_pct = expectedBars > 0 ? Math.Round((currentBarCount / (double)expectedBars) * 100.0, 2) : 0.0,
                            // Late-start handling
                            late_start = isLateStart,
                            missed_breakout = missedBreakout,
                            // Reconstructed range (if available) - ALWAYS LOGGED EVEN IF MISSED BREAKOUT
                            reconstructed_range_high = reconstructedRangeHigh,
                            reconstructed_range_low = reconstructedRangeLow,
                            // Breakout details (if missed breakout occurred)
                            breakout_time_utc = breakoutTimeUtc?.ToString("o"),
                            breakout_time_chicago = breakoutTimeChicago?.ToString("o"),
                            breakout_price = breakoutPrice,
                            breakout_direction = breakoutDirection,
                            // Mode and source info
                            execution_mode = _executionMode.ToString(),
                            note = hydrationNote
                        }));
                    
                    // If missed breakout occurred, log the health event and commit, then return
                    if (missedBreakout)
                    {
                        LogHealth("INFO", "LATE_START_MISSED_BREAKOUT", 
                            $"Starting after slot_time but breakout already occurred at {breakoutTimeChicago.Value:HH:mm:ss} CT. Cannot trade.",
                            new
                            {
                                breakout_time_utc = breakoutTimeUtc.Value.ToString("o"),
                                breakout_time_chicago = breakoutTimeChicago.Value.ToString("o"),
                                breakout_price = breakoutPrice.Value,
                                breakout_direction = breakoutDirection,
                                range_high = reconstructedRangeHigh.Value,
                                range_low = reconstructedRangeLow.Value,
                                slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                                range_start_chicago = RangeStartChicagoTime.ToString("o"),
                                now_chicago = nowChicago.ToString("o")
                            });
                        
                        if (!Commit(utcNow, "NO_TRADE_LATE_START_MISSED_BREAKOUT", "NO_TRADE_LATE_START_MISSED_BREAKOUT")) return;
                        return; // Do not transition to ARMED
                    }

                    // P2.10: bar-based missed-breakout passed — align with unified tick rule vs breakout stops (last bar close if no top-of-book).
                    if (isLateStart && reconstructedRangeHigh.HasValue && reconstructedRangeLow.HasValue)
                    {
                        var brkL = UtilityRoundToTick.RoundToTick(reconstructedRangeHigh.Value + _tickSize, _tickSize);
                        var brkS = UtilityRoundToTick.RoundToTick(reconstructedRangeLow.Value - _tickSize, _tickSize);
                        decimal? hbBid = null, hbAsk = null;
                        if (_executionAdapter != null)
                        {
                            var t = _executionAdapter.GetCurrentMarketPrice(ExecutionInstrument, utcNow);
                            hbBid = t.Bid;
                            hbAsk = t.Ask;
                        }
                        if (!hbBid.HasValue && !hbAsk.HasValue)
                        {
                            var snap = GetBarBufferSnapshot();
                            if (snap.Count > 0)
                            {
                                snap.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
                                var c = snap[snap.Count - 1].Close;
                                hbBid = c;
                                hbAsk = c;
                            }
                        }
                        if (!LogAndEvaluateUnifiedBreakoutEntryValidity(utcNow, "LATE_HYDRATION", failClosedOnMissingQuotes: false,
                                brkL, brkS, hbBid, hbAsk))
                        {
                            if (!Commit(utcNow, "NO_TRADE_LATE_START_UNIFIED_BREAKOUT_INVALID", "NO_TRADE_LATE_START_UNIFIED_BREAKOUT_INVALID")) return;
                            return;
                        }
                    }
                    
                    // HYDRATION_SNAPSHOT: Consolidated snapshot per stream
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, 
                        SlotTimeChicago, SlotTimeUtc,
                        "HYDRATION_SNAPSHOT", "PRE_HYDRATION",
                        new
                        {
                            execution_mode = _executionMode.ToString(),
                            instrument = Instrument,
                            stream_id = Stream,
                            trading_date = TradingDate,
                            barsrequest_raw_count = historicalBarCount + filteredFutureBarCount + filteredPartialBarCount, // Approximate
                            barsrequest_accepted_count = historicalBarCount,
                            live_bar_count = liveBarCount,
                            hydration_source = "BARSREQUEST",
                            transition_reason = barCount > 0 ? "BAR_COUNT" : "TIME_THRESHOLD"
                        }));
                    
                    LogHealth("INFO", "PRE_HYDRATION_COMPLETE", $"Pre-hydration complete (SIM mode) - {barCount} bars total (BarsRequest only)",
                        new
                        {
                            instrument = Instrument,
                            slot = Stream,
                            trading_date = TradingDate,
                            bars_received = barCount,
                            now_chicago = nowChicago.ToString("o"),
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            note = "SIM mode uses BarsRequest only (no CSV files)"
                        });
                    
                    // Log explicit state transition with full context
                    var timeInState = _stateEntryTimeUtc.HasValue 
                        ? (utcNow - _stateEntryTimeUtc.Value).TotalMinutes 
                        : (double?)null;
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "PRE_HYDRATION_TO_ARMED_TRANSITION", "PRE_HYDRATION",
                        new
                        {
                            previous_state = State.ToString(),
                            new_state = "ARMED",
                            bar_count = barCount,
                            now_chicago = nowChicago.ToString("o"),
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            time_in_pre_hydration_minutes = timeInState.HasValue ? Math.Round(timeInState.Value, 2) : (double?)null,
                            transition_reason = barCount > 0 ? "BAR_COUNT" : "TIME_THRESHOLD",
                            note = "Explicit state transition from PRE_HYDRATION to ARMED"
                        }));
                    
                    // Log PRE_HYDRATION_COMPLETE hydration event
                    try
                    {
                        var chicagoNow = _time.ConvertUtcToChicago(utcNow);
                        var preHydrationBarCount = GetBarBufferCount();
                        var preHydrationData = new Dictionary<string, object>
                        {
                            ["bar_count"] = preHydrationBarCount,
                            ["execution_mode"] = _executionMode.ToString(),
                            ["transition_reason"] = "PRE_HYDRATION_COMPLETE_SIM"
                        };
                        
                        var hydrationEvent = new HydrationEvent(
                            eventType: "PRE_HYDRATION_COMPLETE",
                            tradingDay: TradingDate,
                            streamId: Stream,
                            canonicalInstrument: CanonicalInstrument,
                            executionInstrument: ExecutionInstrument,
                            session: Session,
                            slotTimeChicago: SlotTimeChicago,
                            timestampUtc: utcNow,
                            timestampChicago: chicagoNow,
                            state: State.ToString(),
                            data: preHydrationData
                        );
                        
                        _hydrationPersister?.Persist(hydrationEvent);
                    }
                    catch (Exception)
                    {
                        // Fail-safe: hydration logging never throws
                    }
                    
                    Transition(utcNow, StreamState.ARMED, "PRE_HYDRATION_COMPLETE_SIM");
                }
                // Otherwise, wait for more bars from NinjaTrader (they'll be buffered in OnBar)
            }
            else
            {
                // DRYRUN mode: File-based pre-hydration complete, transition to ARMED
                // Note: Hard timeout logic above applies - if timeout triggered, force transition
                // CONSOLIDATED HYDRATION SUMMARY LOG: Forensic snapshot for every day
                int historicalBarCount, liveBarCount, dedupedBarCount, filteredFutureBarCount, filteredPartialBarCount;
                lock (_barBufferLock)
                {
                    historicalBarCount = _historicalBarCount;
                    liveBarCount = _liveBarCount;
                    dedupedBarCount = _dedupedBarCount;
                    filteredFutureBarCount = _filteredFutureBarCount;
                    filteredPartialBarCount = _filteredPartialBarCount;
                }
                
                // LATE-START SAFE HANDLING: Reconstruct range and check for missed breakout
                // Range build window: [range_start, slot_time) - slot_time is EXCLUSIVE
                // Missed-breakout scan window: [slot_time, now] - only if late start
                decimal? reconstructedRangeHigh = null;
                decimal? reconstructedRangeLow = null;
                bool missedBreakout = false;
                DateTimeOffset? breakoutTimeUtc = null;
                DateTimeOffset? breakoutTimeChicago = null;
                decimal? breakoutPrice = null;
                string? breakoutDirection = null;
                bool isLateStart = nowChicago > SlotTimeChicagoTime;
                
                try
                {
                    // Compute range strictly from bars < slot_time (slot_time exclusive)
                    var rangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: SlotTimeUtc);
                    
                    if (rangeResult.Success && rangeResult.RangeHigh.HasValue && rangeResult.RangeLow.HasValue)
                    {
                        reconstructedRangeHigh = rangeResult.RangeHigh.Value;
                        reconstructedRangeLow = rangeResult.RangeLow.Value;
                        
                        // If starting after slot_time, check if breakout already occurred
                        if (isLateStart)
                        {
                            var missedBreakoutResult = CheckMissedBreakout(utcNow, reconstructedRangeHigh.Value, reconstructedRangeLow.Value);
                            missedBreakout = missedBreakoutResult.MissedBreakout;
                            breakoutTimeUtc = missedBreakoutResult.BreakoutTimeUtc;
                            breakoutTimeChicago = missedBreakoutResult.BreakoutTimeChicago;
                            breakoutPrice = missedBreakoutResult.BreakoutPrice;
                            breakoutDirection = missedBreakoutResult.BreakoutDirection;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Non-blocking: log error but continue
                    LogHealth("WARN", "HYDRATION_RANGE_COMPUTE_ERROR", 
                        $"Range computation or missed-breakout check failed: {ex.Message}. Continuing with normal flow.",
                        new { error = ex.ToString() });
                }
                
                // Calculate completeness metrics (non-blocking)
                int expectedBars = 0;
                int expectedFullRangeBars = 0;
                double completenessPct = 0.0;
                try
                {
                    var hydrationEndChicago = nowChicago < SlotTimeChicagoTime ? nowChicago : SlotTimeChicagoTime;
                    var rangeDurationMinutes = (hydrationEndChicago - RangeStartChicagoTime).TotalMinutes;
                    var fullRangeDurationMinutes = (SlotTimeChicagoTime - RangeStartChicagoTime).TotalMinutes;
                    
                    expectedBars = Math.Max(0, (int)Math.Floor(rangeDurationMinutes));
                    expectedFullRangeBars = Math.Max(0, (int)Math.Floor(fullRangeDurationMinutes));
                    
                    // Note: completenessPct will be recalculated using currentBarCount below
                    if (expectedBars > 0)
                    {
                        completenessPct = Math.Min(100.0, (barCount / (double)expectedBars) * 100.0);
                    }
                }
                catch (Exception ex)
                {
                    // Non-blocking: metrics calculation failed, continue without them
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "HYDRATION_COMPLETENESS_CALC_ERROR", State.ToString(),
                        new { error = ex.Message, note = "Completeness calculation failed, continuing without metrics" }));
                }
                
                // Log forced transition if hard timeout triggered
                if (shouldForceTransition)
                {
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "PRE_HYDRATION_FORCED_TRANSITION", State.ToString(),
                        new
                        {
                            reason = forceTransitionReason,
                            trading_date = TradingDate,
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            range_start_date = RangeStartChicagoTime.Date.ToString("yyyy-MM-dd"),
                            hard_timeout_chicago = hardTimeoutChicago.ToString("o"),
                            minutes_past_range_start = Math.Round(minutesPastRangeStart, 2),
                            bar_count = barCount,
                            execution_mode = _executionMode.ToString(),
                            note = "Liveness guarantee: PRE_HYDRATION forced to ARMED after RangeStartChicagoTime + 1 minute (range-start-relative)"
                        }));
                }
                
                // DEBUG: Log boundary contract to prevent regressions
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "HYDRATION_BOUNDARY_CONTRACT", State.ToString(),
                    new
                    {
                        range_build_window = $"[{RangeStartChicagoTime:HH:mm:ss}, {SlotTimeChicagoTime:HH:mm:ss})",
                        range_build_window_note = "slot_time is EXCLUSIVE for range building",
                        missed_breakout_scan_window = isLateStart ? $"[{SlotTimeChicagoTime:HH:mm:ss}, {nowChicago:HH:mm:ss}]" : "N/A (not late start)",
                        missed_breakout_scan_note = isLateStart ? "Only checked if now > slot_time" : "Not applicable",
                        note = "Boundary contract for range reconstruction and missed-breakout detection"
                    }));
                
                // CRITICAL FIX: Re-read barCount right before logging HYDRATION_SUMMARY
                // barCount was captured at the start of HandlePreHydrationState(), but bars are added
                // asynchronously via AddBarToBuffer() from BarsRequest callbacks or live feed.
                // We must read the current buffer count to get accurate loaded_bars.
                int currentBarCount = GetBarBufferCount();
                
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "HYDRATION_SUMMARY", "PRE_HYDRATION",
                    new
                    {
                        stream_id = Stream,
                        canonical_instrument = CanonicalInstrument,
                        instrument = Instrument,
                        slot = Stream,
                        trading_date = TradingDate,
                        total_bars_in_buffer = currentBarCount,
                        // Bar source breakdown
                        historical_bar_count = historicalBarCount,
                        live_bar_count = liveBarCount,
                        deduped_bar_count = dedupedBarCount,
                        filtered_future_bar_count = filteredFutureBarCount,
                        filtered_partial_bar_count = filteredPartialBarCount,
                        // Timing context
                        now_chicago = nowChicago.ToString("o"),
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                        // Completeness metrics
                        expected_bars = expectedBars,
                        expected_full_range_bars = expectedFullRangeBars,
                        loaded_bars = currentBarCount,
                        completeness_pct = expectedBars > 0 ? Math.Round((currentBarCount / (double)expectedBars) * 100.0, 2) : 0.0,
                        // Late-start handling
                        late_start = isLateStart,
                        missed_breakout = missedBreakout,
                        // Reconstructed range (if available)
                        reconstructed_range_high = reconstructedRangeHigh,
                        reconstructed_range_low = reconstructedRangeLow,
                        // Mode and source info
                        execution_mode = _executionMode.ToString(),
                        forced_transition = shouldForceTransition,
                        note = "Consolidated hydration summary - forensic snapshot at PRE_HYDRATION → ARMED transition. " +
                               "This log captures all bar sources, filtering, deduplication statistics, completeness metrics, and late-start handling for debugging and auditability."
                    }));
                
                // HYDRATION_SNAPSHOT: Consolidated snapshot per stream (DRYRUN mode)
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, 
                    SlotTimeChicago, SlotTimeUtc,
                    "HYDRATION_SNAPSHOT", "PRE_HYDRATION",
                    new
                    {
                        execution_mode = _executionMode.ToString(),
                        instrument = Instrument,
                        stream_id = Stream,
                        trading_date = TradingDate,
                        barsrequest_raw_count = historicalBarCount + filteredFutureBarCount + filteredPartialBarCount, // Approximate
                        barsrequest_accepted_count = historicalBarCount,
                        live_bar_count = liveBarCount,
                        hydration_source = "CSV",
                        transition_reason = shouldForceTransition ? "HARD_TIMEOUT" : "PRE_HYDRATION_COMPLETE"
                    }));
                
                // Log PRE_HYDRATION_COMPLETE hydration event
                try
                {
                    var chicagoNow = _time.ConvertUtcToChicago(utcNow);
                    var preHydrationBarCount = GetBarBufferCount();
                    var preHydrationData = new Dictionary<string, object>
                    {
                        ["bar_count"] = preHydrationBarCount,
                        ["execution_mode"] = _executionMode.ToString(),
                        ["transition_reason"] = shouldForceTransition ? "PRE_HYDRATION_FORCED_TIMEOUT" : "PRE_HYDRATION_COMPLETE",
                        ["historical_bar_count"] = historicalBarCount,
                        ["live_bar_count"] = liveBarCount,
                        ["deduped_bar_count"] = dedupedBarCount,
                        ["filtered_future_bar_count"] = filteredFutureBarCount,
                        ["filtered_partial_bar_count"] = filteredPartialBarCount
                    };
                    
                    var hydrationEvent = new HydrationEvent(
                        eventType: "PRE_HYDRATION_COMPLETE",
                        tradingDay: TradingDate,
                        streamId: Stream,
                        canonicalInstrument: CanonicalInstrument,
                        executionInstrument: ExecutionInstrument,
                        session: Session,
                        slotTimeChicago: SlotTimeChicago,
                        timestampUtc: utcNow,
                        timestampChicago: chicagoNow,
                        state: State.ToString(),
                        data: preHydrationData
                    );
                    
                    _hydrationPersister?.Persist(hydrationEvent);
                }
                catch (Exception)
                {
                    // Fail-safe: hydration logging never throws
                }

                // P2.10: after bar-based missed-breakout scan, unify with same tick rule (DRYRUN path; SIM path above).
                if (!missedBreakout && isLateStart && reconstructedRangeHigh.HasValue && reconstructedRangeLow.HasValue)
                {
                    var brkL = UtilityRoundToTick.RoundToTick(reconstructedRangeHigh.Value + _tickSize, _tickSize);
                    var brkS = UtilityRoundToTick.RoundToTick(reconstructedRangeLow.Value - _tickSize, _tickSize);
                    decimal? hbBid = null, hbAsk = null;
                    if (_executionAdapter != null)
                    {
                        var t = _executionAdapter.GetCurrentMarketPrice(ExecutionInstrument, utcNow);
                        hbBid = t.Bid;
                        hbAsk = t.Ask;
                    }
                    if (!hbBid.HasValue && !hbAsk.HasValue)
                    {
                        var snap = GetBarBufferSnapshot();
                        if (snap.Count > 0)
                        {
                            snap.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
                            var c = snap[snap.Count - 1].Close;
                            hbBid = c;
                            hbAsk = c;
                        }
                    }
                    if (!LogAndEvaluateUnifiedBreakoutEntryValidity(utcNow, "LATE_HYDRATION", failClosedOnMissingQuotes: false,
                            brkL, brkS, hbBid, hbAsk))
                    {
                        if (!Commit(utcNow, "NO_TRADE_LATE_START_UNIFIED_BREAKOUT_INVALID", "NO_TRADE_LATE_START_UNIFIED_BREAKOUT_INVALID")) return;
                        return;
                    }
                }
                
                Transition(utcNow, StreamState.ARMED, shouldForceTransition ? "PRE_HYDRATION_FORCED_TIMEOUT" : "PRE_HYDRATION_COMPLETE");
            }
        }
    }

    /// <summary>
    /// Handle ARMED state logic.
    /// </summary>
    private void HandleArmedState(DateTimeOffset utcNow)
    {
                // Require pre-hydration completion before entering RANGE_BUILDING
                if (!_preHydrationComplete)
                {
                    // Should not happen - pre-hydration should complete before ARMED
                    LogHealth("ERROR", "INVARIANT_VIOLATION", "ARMED state reached without pre-hydration completion",
                        new { instrument = Instrument, slot = Stream });
                    return; // Skip processing if invariant violated
                }
                
                // DIAGNOSTIC: Log time comparison details periodically while waiting for range start
                var timeUntilRangeStart = RangeStartUtc - utcNow;
                var timeUntilSlot = SlotTimeUtc - utcNow;
                
                // Log diagnostic info every 5 minutes while waiting, or if we're past range start time
                var shouldLogArmedDiagnostic = !_lastHeartbeatUtc.HasValue || 
                    (utcNow - _lastHeartbeatUtc.Value).TotalMinutes >= 5 ||
                    utcNow >= RangeStartUtc;
                
                if (shouldLogArmedDiagnostic)
                {
                    _lastHeartbeatUtc = utcNow;
                    var barCount = GetBarBufferCount();
                    
                    LogHealth("INFO", "ARMED_STATE_DIAGNOSTIC", 
                        $"ARMED state diagnostic - waiting for range start. Time until range start: {timeUntilRangeStart.TotalMinutes:F1} min, Time until slot: {timeUntilSlot.TotalMinutes:F1} min",
                        new 
                        { 
                            utc_now = utcNow.ToString("o"),
                            range_start_utc = RangeStartUtc.ToString("o"),
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            slot_time_utc = SlotTimeUtc.ToString("o"),
                            slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                            time_until_range_start_minutes = timeUntilRangeStart.TotalMinutes,
                            time_until_slot_minutes = timeUntilSlot.TotalMinutes,
                            pre_hydration_complete = _preHydrationComplete,
                            bar_buffer_count = barCount,
                            can_transition = utcNow >= RangeStartUtc
                        });
                }
                
                if (utcNow >= RangeStartUtc)
                {
                    // Check if market is closed - if so, commit as NO_TRADE_MARKET_CLOSE instead of transitioning to RANGE_BUILDING
                    if (utcNow >= MarketCloseUtc)
                    {
                        if (TryCommitCompletedTradeAtMarketClose(utcNow)) return;
                        if (HasEntryFillForCurrentStream()) return;
                        LogNoTradeMarketClose(utcNow);
                        if (!Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE")) return;
                        return;
                    }
                    
                    // Check if there are bars available to build a range from
                    var barCount = GetBarBufferCount();
                    if (barCount == 0)
                    {
                        // No bars available - wait in ARMED state until bars arrive or market closes
                        // Log diagnostic info to help understand why we're waiting (rate-limited to once per 5 minutes)
                        var shouldLogWaitingForBars = !_lastArmedWaitingForBarsLogUtc.HasValue || 
                            (utcNow - _lastArmedWaitingForBarsLogUtc.Value).TotalMinutes >= 5.0;
                        
                        if (shouldLogWaitingForBars)
                        {
                            _lastArmedWaitingForBarsLogUtc = utcNow;
                            LogHealth("INFO", "ARMED_WAITING_FOR_BARS", $"Range start time reached but no bars available yet. Waiting for bars or market close.",
                                new { 
                                    range_start_chicago = RangeStartChicagoTime.ToString("o"), 
                                    slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                                    utc_now = utcNow.ToString("o"),
                                    range_start_utc = RangeStartUtc.ToString("o"),
                                    market_close_utc = MarketCloseUtc.ToString("o"),
                                    time_since_range_start_minutes = (utcNow - RangeStartUtc).TotalMinutes,
                                    bar_count = barCount
                                });
                        }
                        return; // Stay in ARMED state
                    }
                    
                    // A) Strategy lifecycle: New range window started
                    LogHealth("INFO", "RANGE_WINDOW_STARTED", $"Range window started for slot {SlotTimeChicago}",
                        new { 
                            range_start_chicago = RangeStartChicagoTime.ToString("o"), 
                            slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                            utc_now = utcNow.ToString("o"),
                            range_start_utc = RangeStartUtc.ToString("o"),
                            time_since_range_start_minutes = (utcNow - RangeStartUtc).TotalMinutes
                        });
                    
                    Transition(utcNow, StreamState.RANGE_BUILDING, "RANGE_BUILD_START");
                    
                    // Reset logging flags when entering RANGE_BUILDING state
                    _lastRangeComputeFailedLogUtc = null;
                    
                    // Persist RANGE_BUILDING snapshot for restart recovery
                    var barsAtStart = GetBarBufferSnapshot();
                    if (barsAtStart.Count > 0)
                    {
                        var lastBarAtStart = barsAtStart[barsAtStart.Count - 1];
                        PersistRangeBuildingSnapshot(lastBarAtStart.TimestampUtc);
                    }
                    
                    // Request range lock when slot_time is reached
                    // State handlers may REQUEST but not PERFORM computation
                    if (utcNow >= SlotTimeUtc && !_rangeLocked)
                    {
                        if (!TryLockRange(utcNow))
                        {
                            // Locking failed - will retry on next tick
                            return;
                        }
                    }
                }
    }

    /// <summary>
    /// Handle RANGE_BUILDING state logic.
    /// </summary>
    private void HandleRangeBuildingState(DateTimeOffset utcNow)
    {
        // Check for market close cutoff (all execution modes)
        // If market has closed and no entry detected, commit as NO_TRADE_MARKET_CLOSE
        if (utcNow >= MarketCloseUtc)
        {
            if (TryCommitCompletedTradeAtMarketClose(utcNow)) return;
            if (HasEntryFillForCurrentStream()) return;
            LogNoTradeMarketClose(utcNow);
            if (!Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE")) return;
            return;
        }
        
        // Defensive check: ensure bars are available (should not happen with our fix, but adds safety)
        var barCount = GetBarBufferCount();
        if (barCount == 0)
        {
            // Should not happen (our fix prevents this), but defensive check
            LogHealth("WARN", "RANGE_BUILDING_NO_BARS", 
                "RANGE_BUILDING state reached with no bars available. This should not happen.",
                new { bar_count = barCount });
            // Wait for bars or market close
            return;
        }
        
        // B) Heartbeat / watchdog (throttled)
        if (!_lastHeartbeatUtc.HasValue || (utcNow - _lastHeartbeatUtc.Value).TotalMinutes >= HEARTBEAT_INTERVAL_MINUTES)
        {
            _lastHeartbeatUtc = utcNow;
            int liveBarCount = GetBarBufferCount();
            
            LogHealth("INFO", "HEARTBEAT", $"Stream heartbeat - state={State}, live_bars={liveBarCount}, range_invalidated={_rangeInvalidated}",
                new
                {
                    state = State.ToString(),
                    live_bar_count = liveBarCount,
                    range_invalidated = _rangeInvalidated,
                    largest_single_gap_minutes = _largestSingleGapMinutes,
                    total_gap_minutes = _totalGapMinutes,
                    execution_mode = _executionMode.ToString(),
                    // Track current range high/low if computed
                    range_high = RangeHigh,
                    range_low = RangeLow,
                    range_size = RangeHigh.HasValue && RangeLow.HasValue ? (decimal?)(RangeHigh.Value - RangeLow.Value) : (decimal?)null,
                    range_locked = _rangeLocked
                });
        }
        
        // C) Data feed anomaly: Check for stalled data feed
        if (_lastBarReceivedUtc.HasValue && (utcNow - _lastBarReceivedUtc.Value).TotalMinutes >= DATA_FEED_STALL_THRESHOLD_MINUTES)
        {
            LogHealth("WARN", "DATA_FEED_STALL", $"No live bars received for {(utcNow - _lastBarReceivedUtc.Value).TotalMinutes:F1} minutes during active range window",
                new
                {
                    minutes_since_last_bar = (utcNow - _lastBarReceivedUtc.Value).TotalMinutes,
                    last_bar_utc = _lastBarReceivedUtc.Value.ToString("o"),
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o")
                });
            _lastBarReceivedUtc = utcNow; // Reset to prevent spam
        }
        
        // D) Bar flow stalled: Check for stalled bar flow during active trading window
        // Trigger when expected bar flow stops (hardcode 5 minutes initially, make config-driven later)
        const double BAR_FLOW_STALLED_THRESHOLD_MINUTES = 5.0;
        if (_lastBarReceivedUtc.HasValue && 
            (utcNow - _lastBarReceivedUtc.Value).TotalMinutes >= BAR_FLOW_STALLED_THRESHOLD_MINUTES &&
            State != StreamState.DONE && State != StreamState.SUSPENDED_DATA_INSUFFICIENT)
        {
            // Only log BAR_FLOW_STALLED if we're in an active trading window
            var isInActiveWindow = State == StreamState.RANGE_BUILDING || 
                                   State == StreamState.ARMED || 
                                   State == StreamState.RANGE_LOCKED;
            
            if (isInActiveWindow)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BAR_FLOW_STALLED", State.ToString(),
                    new
                    {
                        minutes_since_last_bar = (utcNow - _lastBarReceivedUtc.Value).TotalMinutes,
                        last_bar_utc = _lastBarReceivedUtc.Value.ToString("o"),
                        state = State.ToString(),
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                        threshold_minutes = BAR_FLOW_STALLED_THRESHOLD_MINUTES
                    }));
                _lastBarReceivedUtc = utcNow; // Reset to prevent spam
            }
        }
        
        // DIAGNOSTIC: Log slot gate evaluation (only if diagnostic logs enabled, and only on state change or rate limit)
        if (_enableDiagnosticLogs)
        {
            var gateDecision = utcNow >= SlotTimeUtc && !_rangeLocked;
            var gateStateChanged = gateDecision != _lastSlotGateState;
            
            // Log if state changed or rate limit exceeded
            var shouldLog = gateStateChanged || 
                           !_lastSlotGateDiagnostic.HasValue || 
                           (utcNow - _lastSlotGateDiagnostic.Value).TotalSeconds >= _slotGateDiagnosticRateLimitSeconds;
            
            if (shouldLog)
            {
                _lastSlotGateDiagnostic = utcNow;
                _lastSlotGateState = gateDecision;
                var comparisonUsed = $"utcNow ({utcNow:o}) >= SlotTimeUtc ({SlotTimeUtc:o}) && !_rangeLocked ({!_rangeLocked})";
                var currentTimeChicagoDiag = _time.ConvertUtcToChicago(utcNow);
                
                var barBufferCount = GetBarBufferCount();
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "SLOT_GATE_DIAGNOSTIC", State.ToString(),
                    new
                    {
                        now_utc = utcNow.ToString("o"),
                        now_chicago = currentTimeChicagoDiag.ToString("o"),
                        slot_time_chicago = SlotTimeChicago,
                        slot_time_utc = SlotTimeUtc.ToString("o"),
                        comparison_used = comparisonUsed,
                        decision_result = gateDecision,
                        state_changed = gateStateChanged,
                        stream_id = Stream,
                        trading_date = TradingDate,
                        range_locked_flag = _rangeLocked,
                        bar_buffer_count = barBufferCount,
                        time_until_slot_seconds = gateDecision ? 0 : (SlotTimeUtc - utcNow).TotalSeconds
                    }));
            }
        }
        
        if (utcNow >= SlotTimeUtc)
        {
            // Gap tolerance invalidation is now disabled - gaps are logged but do not invalidate ranges
            // Previously, _rangeInvalidated would commit the stream, but this is now disabled
            // Gaps are still tracked and logged via BAR_GAP_DETECTED and GAP_TOLERANCE_VIOLATION events
            // but ranges are no longer invalidated due to DATA_FEED_FAILURE gaps
            // if (_rangeInvalidated)
            // {
            //     // G) "Nothing happened" explanation: Trade blocked due to gap violation
            //     LogSlotEndSummary(utcNow, "RANGE_INVALIDATED", false, false, "Range invalidated due to gap tolerance violation");
            //     Commit(utcNow, "RANGE_INVALIDATED", "Gap tolerance violation");
            //     return;
            // }
            
            // Request range lock when slot_time is reached
            // State handlers may REQUEST but not PERFORM computation
            if (utcNow >= SlotTimeUtc && !_rangeLocked)
            {
                if (!TryLockRange(utcNow))
                {
                    // Locking failed - will retry on next tick
                    return;
                }
            }
            
            // Legacy check: If slot_time passed but range not locked, log error
            if (utcNow >= SlotTimeUtc.AddMinutes(1) && !_rangeLocked && !_rangeInvalidated)
            {
                LogHealth("ERROR", "INVARIANT_VIOLATION", "Slot_time passed without range lock or failure — this should never happen",
                    new
                    {
                        violation = "SLOT_TIME_PASSED_WITHOUT_RESOLUTION",
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                        current_time_chicago = _time.ConvertUtcToChicago(utcNow).ToString("o"),
                        range_locked = _rangeLocked,
                        range_invalidated = _rangeInvalidated
                    });
                
                // NQ2 FIX: Report critical if slot_time passed without range lock
                if (_reportCriticalCallback != null)
                {
                    _reportCriticalCallback("SLOT_TIME_PASSED_WITHOUT_RANGE_LOCK", new Dictionary<string, object>
                    {
                        { "stream", Stream },
                        { "slot_time_chicago", SlotTimeChicagoTime.ToString("o") },
                        { "current_time_chicago", _time.ConvertUtcToChicago(utcNow).ToString("o") },
                        { "range_locked", _rangeLocked },
                        { "range_invalidated", _rangeInvalidated }
                    }, TradingDate);
                }
            }
            
            // Legacy check: If range is locked but state is not RANGE_LOCKED, log critical error.
            // A durable terminal commit intentionally moves RANGE_LOCKED -> DONE and is not a partial lock failure.
            if (_rangeLocked && State != StreamState.RANGE_LOCKED && !(State == StreamState.DONE && _journal.Committed))
            {
                LogHealth("CRITICAL", "RANGE_LOCK_TRANSITION_FAILED", 
                    "Range lock flag is true but state is not RANGE_LOCKED - partial failure detected",
                    new
                    {
                        violation = "PARTIAL_LOCK_FAILURE",
                        range_locked = _rangeLocked,
                        current_state = State.ToString(),
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                        note = "This indicates a fatal error - transition failed after lock was committed"
                    });
            }
            
            // Early return if range is already locked
            if (_rangeLocked)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Handle RANGE_LOCKED state logic.
    /// </summary>
    private void HandleRangeLockedState(DateTimeOffset utcNow)
    {
        EnsureCommittedForPostLockExcursion(utcNow);

        // Phase B: Execute pending recovery action (invariant-based model)
        if (_entryOrderRecoveryState.IsPending && _executionAdapter != null)
        {
            try
            {
                var snap = _executionAdapter.GetAccountSnapshot(utcNow);
                if (ExecutePendingRecoveryAction(snap, utcNow))
                    return; // Action handled (resubmit or cancel sent)
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDERS_RESUBMIT_POSITION_CHECK_ERROR", State.ToString(),
                    new { error = ex.Message, note = "Phase B execution failed - will retry next tick" }));
            }
        }

        // RESTART RECOVERY: Retry stop bracket placement if it failed previously
        // This handles the case where stop orders failed to place (e.g., missing policy expectations)
        // and the strategy was restarted. On restart, we retry placement if:
        // - Stop brackets weren't submitted yet (_stopBracketsSubmittedAtLock = false)
        // - Entry not detected
        // - Before market close
        // - Range and breakout levels are available
        if (!_stopBracketsSubmittedAtLock && !_entryDetected && utcNow < MarketCloseUtc &&
            RangeHigh.HasValue && RangeLow.HasValue &&
            _brkLongRounded.HasValue && _brkShortRounded.HasValue)
        {
            // Check if intents were already submitted (idempotency check)
            var longIntentId = ComputeIntentId("Long", _brkLongRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_LONG");
            var shortIntentId = ComputeIntentId("Short", _brkShortRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_SHORT");
            
            bool alreadySubmitted = false;
            if (_executionJournal != null)
            {
                alreadySubmitted = _executionJournal.IsIntentSubmitted(longIntentId, TradingDate, Stream) ||
                                  _executionJournal.IsIntentSubmitted(shortIntentId, TradingDate, Stream);
            }
            
            if (!alreadySubmitted)
            {
                if (IsPostLockBreakoutSetupExpired())
                {
                    _ = Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED");
                }
                else
                {
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "RESTART_RETRY_STOP_BRACKETS", State.ToString(),
                        new
                        {
                            stream_id = Stream,
                            trading_date = TradingDate,
                            slot_time_chicago = SlotTimeChicago,
                            previous_attempt_failed = true,
                            note = "Retrying stop bracket placement on restart after previous failure"
                        }));
                    SubmitStopEntryBracketsAtLock(utcNow);
                }
            }
        }
        
        // Check for market close cutoff (all execution modes)
        if (utcNow >= MarketCloseUtc)
        {
            if (TryCommitCompletedTradeAtMarketClose(utcNow)) return;
            if (HasEntryFillForCurrentStream()) return;
            LogNoTradeMarketClose(utcNow);
            if (!Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE")) return;
        }
    }
    
    /// <summary>
    /// Helper to compute intent ID for restart recovery check.
    /// </summary>
    private string ComputeIntentId(string direction, decimal entryPrice, DateTimeOffset entryTimeUtc, string triggerReason)
    {
        var intent = new Intent(
            TradingDate,
            Stream,
            Instrument,
            ExecutionInstrument,
            Session,
            SlotTimeChicago,
            direction,
            entryPrice,
            stopPrice: null, // Not needed for intent ID computation
            targetPrice: null,
            beTrigger: null,
            entryTimeUtc,
            triggerReason);
        return intent.ComputeIntentId();
    }

    /// <summary>
    /// STRICT: True only if broker has exactly one valid entry-order set.
    /// Valid set = both orders exist, correct instrument, intent IDs, breakout prices, OCO linkage, no duplicates.
    /// Any incomplete, wrong-price, duplicate, or malformed structure → false.
    /// </summary>
    public bool HasValidEntryOrdersOnBroker(AccountSnapshot snap)
    {
        if (!RangeHigh.HasValue || !RangeLow.HasValue ||
            !_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
            return false;

        var longIntentId = ComputeIntentId("Long", _brkLongRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_LONG");
        var shortIntentId = ComputeIntentId("Short", _brkShortRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_SHORT");
        var brkLong = _brkLongRounded.Value;
        var brkShort = _brkShortRounded.Value;

        var working = snap.WorkingOrders ?? new List<WorkingOrderSnapshot>();
        WorkingOrderSnapshot? longOrder = null;
        WorkingOrderSnapshot? shortOrder = null;
        int longCount = 0, shortCount = 0;

        foreach (var o in working)
        {
            if (!IsSameInstrument(o.Instrument))
                continue;
            var tag = o.Tag ?? "";
            if (string.IsNullOrEmpty(tag) || tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase) || tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase))
                continue;
            var decoded = RobotOrderIds.DecodeIntentId(tag);
            if (decoded == longIntentId) { longCount++; longOrder = o; }
            else if (decoded == shortIntentId) { shortCount++; shortOrder = o; }
        }

        // INVARIANT: Exactly 1 long, 1 short. No partial, no duplicates.
        if (longCount != 1 || shortCount != 1)
            return false;

        // Strict: match expected breakout prices (StopPrice)
        if (longOrder != null && longOrder.StopPrice.HasValue && Math.Abs(longOrder.StopPrice.Value - brkLong) > 0.0001m)
            return false;
        if (shortOrder != null && shortOrder.StopPrice.HasValue && Math.Abs(shortOrder.StopPrice.Value - brkShort) > 0.0001m)
            return false;

        // OCO linkage: both must share same OcoGroup (if OCO is used)
        if (longOrder != null && shortOrder != null)
        {
            var ocoLong = longOrder.OcoGroup ?? "";
            var ocoShort = shortOrder.OcoGroup ?? "";
            if (!string.IsNullOrEmpty(ocoLong) && !string.IsNullOrEmpty(ocoShort) && ocoLong != ocoShort)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Get counts of matching entry orders (excludes protectives). Returns (longCount, shortCount, orderIds).
    /// </summary>
    private (int LongCount, int ShortCount, List<string> OrderIds) GetMatchingEntryOrderCounts(
        AccountSnapshot snap, string longIntentId, string shortIntentId)
    {
        var working = snap.WorkingOrders ?? new List<WorkingOrderSnapshot>();
        int longCount = 0, shortCount = 0;
        var orderIds = new List<string>();

        foreach (var o in working)
        {
            if (!IsSameInstrument(o.Instrument))
                continue;
            var tag = o.Tag ?? "";
            if (string.IsNullOrEmpty(tag) || tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase) || tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase))
                continue;
            var decoded = RobotOrderIds.DecodeIntentId(tag);
            if (decoded == longIntentId) { longCount++; orderIds.Add(o.OrderId ?? ""); }
            else if (decoded == shortIntentId) { shortCount++; orderIds.Add(o.OrderId ?? ""); }
        }
        return (longCount, shortCount, orderIds);
    }

    /// <summary>
    /// Phase A: Audit and classify broker state. Assign recovery action. Do NOT execute.
    /// Returns (reconciled: we checked, needsResubmit: action is ResubmitClean or CancelAndRebuild).
    /// </summary>
    public (bool Reconciled, bool NeedsResubmit) AuditAndClassifyEntryOrders(AccountSnapshot snap, DateTimeOffset utcNow)
    {
        if (Committed || State != StreamState.RANGE_LOCKED)
            return (false, false);
        if (_journal.ExecutionInterruptedByClose)
            return (false, false); // Safe by design: no entry resubmit when waiting for re-entry after forced flatten
        if (_entryDetected || utcNow >= MarketCloseUtc)
            return (false, false);
        if (!RangeHigh.HasValue || !RangeLow.HasValue || !_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
            return (false, false);

        var longIntentId = ComputeIntentId("Long", _brkLongRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_LONG");
        var shortIntentId = ComputeIntentId("Short", _brkShortRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_SHORT");
        var expectedLongPrice = _brkLongRounded.Value;
        var expectedShortPrice = _brkShortRounded.Value;

        var posQty = snap.Positions?.Where(p => IsSameInstrument(p.Instrument)).Sum(p => p.Quantity) ?? 0;
        var classification = ClassifyBrokerState(snap, longIntentId, shortIntentId, expectedLongPrice, expectedShortPrice, posQty);

        _entryOrderRecoveryState.LastClassificationUtc = utcNow;
        _entryOrderRecoveryState.LastClassificationResult = classification.ToString();

        switch (classification)
        {
            case BrokerStateClassification.PositionExists:
                return (true, false);

            case BrokerStateClassification.ValidSetPresent:
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDER_SET_VALID", State.ToString(),
                    new { stream_id = Stream, trading_date = TradingDate, long_intent_id = longIntentId, short_intent_id = shortIntentId }));
                ClearRecoveryAction(utcNow);
                return (true, false);

            case BrokerStateClassification.MissingSet:
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDER_SET_MISSING", State.ToString(),
                    new { stream_id = Stream, trading_date = TradingDate, long_intent_id = longIntentId, short_intent_id = shortIntentId, expected_long_price = expectedLongPrice, expected_short_price = expectedShortPrice }));
                SetRecoveryAction(EntryOrderRecoveryAction.ResubmitClean, "missing", utcNow);
                return (true, true);

            case BrokerStateClassification.BrokenSet:
                var (longCount, shortCount, orderIds) = GetMatchingEntryOrderCounts(snap, longIntentId, shortIntentId);
                var reason = longCount + shortCount == 0 ? "missing" : longCount + shortCount == 1 ? "partial" : "duplicate_or_invalid";
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    longCount + shortCount > 2 ? "ENTRY_ORDER_SET_DUPLICATE_DETECTED" : "ENTRY_ORDER_SET_BROKEN", State.ToString(),
                    new { stream_id = Stream, trading_date = TradingDate, long_intent_id = longIntentId, short_intent_id = shortIntentId, actual_long_count = longCount, actual_short_count = shortCount, order_ids = orderIds, reason }));
                SetRecoveryAction(EntryOrderRecoveryAction.CancelAndRebuild, reason, utcNow);
                return (true, true);
        }

        return (true, false);
    }

    /// <summary>
    /// Phase A backward compatibility: ReconcileEntryOrders delegates to AuditAndClassifyEntryOrders.
    /// </summary>
    public (bool Reconciled, bool NeedsResubmit) ReconcileEntryOrders(AccountSnapshot snap, DateTimeOffset utcNow)
        => AuditAndClassifyEntryOrders(snap, utcNow);

    /// <summary>
    /// Classify broker state for this stream.
    /// </summary>
    public BrokerStateClassification ClassifyBrokerState(AccountSnapshot snap, string longIntentId, string shortIntentId)
    {
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
            return BrokerStateClassification.MissingSet;
        var posQty = snap.Positions?.Where(p => IsSameInstrument(p.Instrument)).Sum(p => p.Quantity) ?? 0;
        return ClassifyBrokerState(snap, longIntentId, shortIntentId, _brkLongRounded.Value, _brkShortRounded.Value, posQty);
    }

    private BrokerStateClassification ClassifyBrokerState(AccountSnapshot snap, string longIntentId, string shortIntentId, decimal expectedLongPrice, decimal expectedShortPrice, int posQty)
    {
        if (posQty != 0 || _entryDetected)
            return BrokerStateClassification.PositionExists;

        var (longCount, shortCount, _) = GetMatchingEntryOrderCounts(snap, longIntentId, shortIntentId);
        var total = longCount + shortCount;

        if (longCount == 1 && shortCount == 1)
        {
            var working = snap.WorkingOrders ?? new List<WorkingOrderSnapshot>();
            WorkingOrderSnapshot? longOrder = null, shortOrder = null;
            foreach (var o in working)
            {
                if (!IsSameInstrument(o.Instrument)) continue;
                var tag = o.Tag ?? "";
                if (string.IsNullOrEmpty(tag) || tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase) || tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase)) continue;
                var decoded = RobotOrderIds.DecodeIntentId(tag);
                if (decoded == longIntentId) longOrder = o;
                else if (decoded == shortIntentId) shortOrder = o;
            }
            if (longOrder != null && longOrder.StopPrice.HasValue && Math.Abs(longOrder.StopPrice.Value - expectedLongPrice) > 0.0001m)
                return BrokerStateClassification.BrokenSet;
            if (shortOrder != null && shortOrder.StopPrice.HasValue && Math.Abs(shortOrder.StopPrice.Value - expectedShortPrice) > 0.0001m)
                return BrokerStateClassification.BrokenSet;
            if (longOrder != null && shortOrder != null)
            {
                var ocoLong = longOrder.OcoGroup ?? "";
                var ocoShort = shortOrder.OcoGroup ?? "";
                if (!string.IsNullOrEmpty(ocoLong) && !string.IsNullOrEmpty(ocoShort) && ocoLong != ocoShort)
                    return BrokerStateClassification.BrokenSet;
            }
            return BrokerStateClassification.ValidSetPresent;
        }

        if (total == 0)
            return BrokerStateClassification.MissingSet;

        return BrokerStateClassification.BrokenSet;
    }

    private void SetRecoveryAction(EntryOrderRecoveryAction action, string reason, DateTimeOffset utcNow)
    {
        _entryOrderRecoveryState = new EntryOrderRecoveryState { Action = action, Reason = reason, IssuedUtc = utcNow };
        _journal.RecoveryAction = action.ToString();
        _journal.RecoveryActionReason = reason;
        _journal.RecoveryActionIssuedUtc = utcNow.ToString("o");
        _journals.Save(_journal);
        _stopBracketsSubmittedAtLock = false;
        _journal.StopBracketsSubmittedAtLock = false;
        _journals.Save(_journal);
    }

    private void ClearRecoveryAction(DateTimeOffset utcNow)
    {
        if (_entryOrderRecoveryState.Action == EntryOrderRecoveryAction.None) return;
        _entryOrderRecoveryState = EntryOrderRecoveryState.None();
        _journal.RecoveryAction = "None";
        _journal.RecoveryActionReason = null;
        _journal.RecoveryActionIssuedUtc = null;
        _journals.Save(_journal);
    }

    /// <summary>
    /// Get broker order IDs for this stream's entry orders (for cancel).
    /// </summary>
    public List<string> GetEntryOrderIdsForStream(AccountSnapshot snap)
    {
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
            return new List<string>();
        var longIntentId = ComputeIntentId("Long", _brkLongRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_LONG");
        var shortIntentId = ComputeIntentId("Short", _brkShortRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_SHORT");
        var (_, _, orderIds) = GetMatchingEntryOrderCounts(snap, longIntentId, shortIntentId);
        return orderIds;
    }

    /// <summary>P2.10: Single tick-distance rule vs breakout stops (parity: <see cref="ParityInstrument.breakout_validity_tolerance_ticks"/>).</summary>
    private int GetBreakoutValidityToleranceTicks()
    {
        if (_spec != null && _spec.TryGetInstrument(Instrument, out var inst))
            return Math.Max(1, inst.breakout_validity_tolerance_ticks);
        return 2;
    }

    /// <summary>P2.10 mandatory core: same math for every path; missing-quote policy is caller-controlled.</summary>
    private bool IsBreakoutStillValidForEntry(
        decimal? bid,
        decimal? ask,
        decimal brkLong,
        decimal brkShort,
        int toleranceTicks,
        bool failClosedOnMissingQuotes)
    {
        if (!bid.HasValue && !ask.HasValue)
            return !failClosedOnMissingQuotes;
        var tolerance = toleranceTicks * _tickSize;
        var longInvalid = ask.HasValue && ask.Value >= brkLong + tolerance;
        var shortInvalid = bid.HasValue && bid.Value <= brkShort - tolerance;
        return !(longInvalid || shortInvalid);
    }

    private enum BreakoutEntrySubmitAction
    {
        Stop,
        Market,
        Reject
    }

    private (BreakoutEntrySubmitAction Action, decimal? DistanceTicks, string? Reason) ResolveBreakoutEntrySubmitAction(
        string direction,
        decimal breakoutPrice,
        decimal? bid,
        decimal? ask,
        int toleranceTicks)
    {
        decimal? marketPrice = string.Equals(direction, "Long", StringComparison.OrdinalIgnoreCase) ? ask : bid;
        if (!marketPrice.HasValue || _tickSize <= 0)
            return (BreakoutEntrySubmitAction.Stop, null, null);

        var crossed = string.Equals(direction, "Long", StringComparison.OrdinalIgnoreCase)
            ? marketPrice.Value >= breakoutPrice
            : marketPrice.Value <= breakoutPrice;
        if (!crossed)
            return (BreakoutEntrySubmitAction.Stop, null, null);

        var distanceTicks = string.Equals(direction, "Long", StringComparison.OrdinalIgnoreCase)
            ? (marketPrice.Value - breakoutPrice) / _tickSize
            : (breakoutPrice - marketPrice.Value) / _tickSize;

        if (distanceTicks <= toleranceTicks)
            return (BreakoutEntrySubmitAction.Market, distanceTicks, "crossed_within_tolerance");

        return (BreakoutEntrySubmitAction.Reject, distanceTicks, "crossed_beyond_tolerance");
    }

    /// <summary>
    /// P2.10 unified gate: logs <c>BREAKOUT_VALIDATED_UNIFIED</c> / <c>BREAKOUT_INVALIDATED_UNIFIED</c>.
    /// Returns true if valid or gate N/A (no breakout levels). Returns false if blocked.
    /// </summary>
    private bool LogAndEvaluateUnifiedBreakoutEntryValidity(
        DateTimeOffset utcNow,
        string path,
        bool failClosedOnMissingQuotes,
        decimal? brkLongOverride = null,
        decimal? brkShortOverride = null,
        decimal? bidOverride = null,
        decimal? askOverride = null)
    {
        var brkLong = brkLongOverride ?? _brkLongRounded;
        var brkShort = brkShortOverride ?? _brkShortRounded;
        if (!brkLong.HasValue || !brkShort.HasValue)
            return true;

        var toleranceTicks = GetBreakoutValidityToleranceTicks();
        decimal? bid = bidOverride;
        decimal? ask = askOverride;
        if (!bid.HasValue && !ask.HasValue && _executionAdapter != null)
        {
            var t = _executionAdapter.GetCurrentMarketPrice(ExecutionInstrument, utcNow);
            bid = t.Bid;
            ask = t.Ask;
        }

        var tolerance = toleranceTicks * _tickSize;
        var valid = IsBreakoutStillValidForEntry(bid, ask, brkLong.Value, brkShort.Value, toleranceTicks, failClosedOnMissingQuotes);
        var longInvalid = ask.HasValue && ask.Value >= brkLong.Value + tolerance;
        var shortInvalid = bid.HasValue && bid.Value <= brkShort.Value - tolerance;
        var missingQuotes = !bid.HasValue && !ask.HasValue;

        if (valid)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "BREAKOUT_VALIDATED_UNIFIED", State.ToString(),
                new
                {
                    stream_id = Stream,
                    path,
                    current_bid = bid,
                    current_ask = ask,
                    brk_long = brkLong,
                    brk_short = brkShort,
                    tolerance_ticks = toleranceTicks,
                    note = "Unified breakout entry validity — proceed"
                }));
            return true;
        }

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "BREAKOUT_INVALIDATED_UNIFIED", State.ToString(),
            new
            {
                stream_id = Stream,
                path,
                current_bid = bid,
                current_ask = ask,
                brk_long = brkLong,
                brk_short = brkShort,
                tolerance_ticks = toleranceTicks,
                long_invalid = longInvalid,
                short_invalid = shortInvalid,
                missing_quotes = missingQuotes,
                fail_closed_on_missing_quotes = failClosedOnMissingQuotes,
                reason = missingQuotes && failClosedOnMissingQuotes ? "missing_quotes_fail_closed" : "beyond_breakout_tolerance",
                note = "Unified breakout entry validity — block"
            }));
        return false;
    }

    /// <summary>
    /// Phase B: Execute pending recovery action. Runs pre-execution gate, then resubmit or cancel-rebuild.
    /// </summary>
    public bool ExecutePendingRecoveryAction(AccountSnapshot snap, DateTimeOffset utcNow)
    {
        if (_entryOrderRecoveryState.Action == EntryOrderRecoveryAction.None)
            return false;
        if (Committed || State != StreamState.RANGE_LOCKED || _journal.ExecutionInterruptedByClose || _entryDetected || utcNow >= MarketCloseUtc)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "ENTRY_ORDER_ACTION_CLEARED_STREAM_INELIGIBLE", State.ToString(),
                new { stream_id = Stream, reason = "stream_no_longer_eligible" }));
            ClearRecoveryAction(utcNow);
            return false;
        }
        if (IsPostLockBreakoutSetupExpired())
        {
            ClearRecoveryAction(utcNow);
            _ = Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED");
            return false;
        }
        var posQty = snap.Positions?.Where(p => IsSameInstrument(p.Instrument)).Sum(p => p.Quantity) ?? 0;
        if (posQty != 0)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "ENTRY_ORDER_SET_RESUBMIT_SKIPPED_POSITION_EXISTS", State.ToString(),
                new { stream_id = Stream, position_qty = posQty }));
            ClearRecoveryAction(utcNow);
            return false;
        }
        if (HasValidEntryOrdersOnBroker(snap))
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "ENTRY_ORDER_SET_RESUBMIT_SKIPPED_VALID_EXISTS", State.ToString(),
                new { stream_id = Stream }));
            ClearRecoveryAction(utcNow);
            return false;
        }

        if (_entryOrderRecoveryState.Action == EntryOrderRecoveryAction.CancelAndRebuild)
        {
            var orderIds = GetEntryOrderIdsForStream(snap);
            if (orderIds.Count > 0)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDER_SET_CANCEL_REQUESTED", State.ToString(),
                    new { stream_id = Stream, order_ids = orderIds }));
                _executionAdapter?.CancelOrders(orderIds, utcNow);
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDER_SET_REBUILD_BLOCKED_CANCEL_INCOMPLETE", State.ToString(),
                    new { stream_id = Stream, note = "Cancel sent; will retry rebuild on next cycle after confirmation" }));
                return true; // Don't clear - wait for next cycle when orders are gone
            }
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "ENTRY_ORDER_SET_REBUILD_REQUESTED", State.ToString(),
                new { stream_id = Stream }));
            _entryOrderRecoveryState = new EntryOrderRecoveryState { Action = EntryOrderRecoveryAction.ResubmitClean, Reason = _entryOrderRecoveryState.Reason + "_after_cancel", IssuedUtc = utcNow };
            _journal.RecoveryAction = "ResubmitClean";
            _journal.RecoveryActionReason = _entryOrderRecoveryState.Reason;
            _journal.RecoveryActionIssuedUtc = utcNow.ToString("o");
            _journals.Save(_journal);
        }
        if (_entryOrderRecoveryState.Action == EntryOrderRecoveryAction.ResubmitClean)
        {
            SubmitStopEntryBracketsAtLock(utcNow);
            if (_stopBracketsSubmittedAtLock)
            {
                ClearRecoveryAction(utcNow);
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDERS_RESUBMITTED", State.ToString(),
                    new { stream_id = Stream, reason = "recovery_resubmit" }));
            }
            return true;
        }
        return false;
    }

    public void Arm(DateTimeOffset utcNow)
    {
        if (_journal.Committed)
        {
            State = StreamState.DONE;
            return;
        }
        
        // CRITICAL: If range was restored from logs, skip normal flow
        // Restoration happens in constructor before Arm() is called
        // If _rangeLocked == true, we should already be in RANGE_LOCKED state
        if (_rangeLocked)
        {
            // Range was restored - verify state is correct
            if (State != StreamState.RANGE_LOCKED)
            {
                LogHealth("ERROR", "RANGE_LOCKED_STATE_MISMATCH", 
                    "Range lock restored but state is not RANGE_LOCKED",
                    new
                    {
                        range_locked = _rangeLocked,
                        current_state = State.ToString(),
                        note = "Restoration should have set state to RANGE_LOCKED"
                    });
                // Force transition to correct state
                Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED_RESTORED_FIX");
            }
            EnsureCommittedForPostLockExcursion(utcNow);
            // Skip normal flow - restoration already completed
            return;
        }
        
        // A) Strategy lifecycle: New slot / stream armed
        LogHealth("INFO", "STREAM_ARMED", $"Stream armed for slot {SlotTimeChicago}",
            new { slot_time_chicago = SlotTimeChicago, instrument = Instrument, session = Session });
        
        // Reset pre-hydration and gap tracking state on re-arming
        // SIM mode: Uses NinjaTrader historical bars (buffered in OnBar during PRE_HYDRATION)
        // DRYRUN mode: Uses file-based pre-hydration
        _preHydrationComplete = false;
        
        _largestSingleGapMinutes = 0.0;
        _totalGapMinutes = 0.0;
        _lastBarOpenChicago = null;
        _rangeInvalidated = false;
        _rangeInvalidatedNotified = false; // Reset notification flag on new slot
        _slotEndSummaryLogged = false;
        _lastHeartbeatUtc = null;
        _lastBarReceivedUtc = null;
        _lastBarTimestampUtc = null;
        _lastPreHydrationHandlerTraceUtc = null;
        
        // Streams start in PRE_HYDRATION for both SIM and DRYRUN
        // SIM mode: Uses NinjaTrader historical bars (buffered in OnBar)
        // DRYRUN mode: Uses file-based pre-hydration
        Transition(utcNow, StreamState.PRE_HYDRATION, "STREAM_ARMED");
    }

    public void EnterRecoveryManage(DateTimeOffset utcNow, string reason)
    {
        if (_journal.Committed)
        {
            State = StreamState.DONE;
            return;
        }
        
        // Reset range lock flags for explicit recovery restart
        _rangeLocked = false;
        _rangeLockCommitted = false;
        _rangeLockAttemptedAtUtc = null;
        _rangeLockFailureCount = 0;
        _breakoutLevelsMissing = false;
        
        // Transition directly to DONE instead of RECOVERY_MANAGE
        if (!Commit(utcNow, "STREAM_STAND_DOWN", "STREAM_STAND_DOWN")) return;
    }

    private DateTimeOffset _lastExecutionGateEvalBarUtc = DateTimeOffset.MinValue;
    private const int EXECUTION_GATE_EVAL_RATE_LIMIT_SECONDS = 60; // Log once per minute max

    public void OnBar(DateTimeOffset barUtc, decimal open, decimal high, decimal low, decimal close, DateTimeOffset utcNow, bool isHistorical = false)
    {
        // CRITICAL: Convert bar timestamp to Chicago time explicitly
        // Do NOT assume barUtc is UTC or Chicago - conversion must be explicit
        var barChicagoTime = _time.ConvertUtcToChicago(barUtc);
        
        // SAFETY CHECK: Filter bars by trading date (RobotEngine already filters, but this is defensive)
        var barTradingDate = _time.GetChicagoDateToday(barUtc).ToString("yyyy-MM-dd");
        if (barTradingDate != TradingDate)
        {
            // Bar is from wrong trading date - ignore it
            // This should not happen (RobotEngine filters first), but this is a defensive check
            return;
        }
        
        // Early return if stream is committed (optimization - avoid unnecessary processing)
        if (_journal.Committed)
        {
            return;
        }
        
        // Liveness Fix: Decouple bar buffering from stream state
        // Bars within range window [RangeStartChicagoTime, SlotTimeChicagoTime) must always be buffered,
        // regardless of stream state. State should only gate decisions (e.g., range computation, execution),
        // not data ingestion. Bar timestamps represent bar end time for range window comparisons.
        
        // DIAGNOSTIC: Log bar reception + admission proof (only if diagnostic logs enabled, rate-limited).
        // BAR_ADMISSION_PROOF was previously unconditional and dominated log volume during busy windows.
        bool shouldLogBar = false;
        if (_enableDiagnosticLogs)
        {
            shouldLogBar = !_lastBarDiagnosticTime.HasValue ||
                          (utcNow - _lastBarDiagnosticTime.Value).TotalSeconds >= _barDiagnosticRateLimitSeconds;

            if (shouldLogBar)
            {
                _lastBarDiagnosticTime = utcNow;
                var inRange = barChicagoTime >= RangeStartChicagoTime && barChicagoTime <= SlotTimeChicagoTime;
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BAR_RECEIVED_DIAGNOSTIC", State.ToString(),
                    new
                    {
                        bar_utc = barUtc.ToString("o"),
                        bar_utc_kind = barUtc.DateTime.Kind.ToString(),
                        bar_chicago = barChicagoTime.ToString("o"),
                        bar_chicago_offset = barChicagoTime.Offset.ToString(),
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        range_end_chicago = SlotTimeChicagoTime.ToString("o"),
                        range_start_utc = RangeStartUtc.ToString("o"),
                        range_end_utc = SlotTimeUtc.ToString("o"),
                        in_range_window = inRange,
                        bar_buffer_count = _barBuffer.Count,
                        time_until_slot_seconds = (SlotTimeUtc - utcNow).TotalSeconds
                    }));

                var barSourceStr = isHistorical ? "BARSREQUEST" : "LIVE";
                var comparisonResult = barChicagoTime >= RangeStartChicagoTime && barChicagoTime <= SlotTimeChicagoTime;
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BAR_ADMISSION_PROOF", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        canonical_instrument = CanonicalInstrument,
                        instrument = Instrument,
                        bar_time_raw_utc = barUtc.ToString("o"),
                        bar_time_raw_kind = barUtc.DateTime.Kind.ToString(),
                        bar_time_chicago = barChicagoTime.ToString("o"),
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                        comparison_result = comparisonResult,
                        comparison_detail = comparisonResult
                            ? $"bar_chicago ({barChicagoTime:HH:mm:ss}) >= range_start ({RangeStartChicagoTime:HH:mm:ss}) AND bar_chicago <= slot_time ({SlotTimeChicagoTime:HH:mm:ss})"
                            : $"bar_chicago ({barChicagoTime:HH:mm:ss}) NOT in [range_start ({RangeStartChicagoTime:HH:mm:ss}), slot_time ({SlotTimeChicagoTime:HH:mm:ss})]",
                        bar_source = barSourceStr,
                        note = "Diagnostic proof log (gated + rate-limited with BAR_RECEIVED_DIAGNOSTIC)"
                    }));
            }
        }
        
        // C) Data feed anomaly: Check for out-of-order bars
        if (_lastBarTimestampUtc.HasValue && barUtc < _lastBarTimestampUtc.Value)
        {
            LogHealth("WARN", "DATA_FEED_OUT_OF_ORDER", "Bar received out of chronological order",
                new
                {
                    bar_utc = barUtc.ToString("o"),
                    previous_bar_utc = _lastBarTimestampUtc.Value.ToString("o"),
                    gap_minutes = (barUtc - _lastBarTimestampUtc.Value).TotalMinutes
                });
        }
        
        // C) Data feed anomaly: Check for bars outside expected session window
        if (barChicagoTime < RangeStartChicagoTime.AddMinutes(-5) || barChicagoTime > SlotTimeChicagoTime.AddMinutes(5))
        {
            LogHealth("WARN", "DATA_FEED_OUTSIDE_WINDOW", "Bar timestamp outside expected session window",
                new
                {
                    bar_chicago = barChicagoTime.ToString("o"),
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                    minutes_before_start = (barChicagoTime - RangeStartChicagoTime).TotalMinutes,
                    minutes_after_end = (barChicagoTime - SlotTimeChicagoTime).TotalMinutes
                });
        }
        
        // Update last bar received timestamp for data feed health monitoring
        _lastBarReceivedUtc = utcNow;
        _lastBarTimestampUtc = barUtc;

        // Post-slot OHLC envelope (all states): feeds pre-submit breakout scan; journal flags are optional memory.
        if (barUtc > SlotTimeUtc && high >= low && close >= low && close <= high)
        {
            if (!_postSlotExcursionHasSamples)
            {
                _postSlotExcursionHasSamples = true;
                _postSlotMaxHighSinceSlot = high;
                _postSlotMinLowSinceSlot = low;
            }
            else
            {
                if (high > _postSlotMaxHighSinceSlot) _postSlotMaxHighSinceSlot = high;
                if (low < _postSlotMinLowSinceSlot) _postSlotMinLowSinceSlot = low;
            }
        }
        
        // Buffer bars that fall within [range_start, slot_time] using Chicago time comparison
        // Bar timestamps represent OPEN time (converted from NinjaTrader close time for Analyzer parity)
        // Range window is defined in Chicago time to match trading session semantics
        // State-independent buffering: Always buffer bars within range window regardless of state
        // CRITICAL FIX: Include slot_time bar (<= instead of <) so range lock check runs when slot_time bar arrives

        if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime <= SlotTimeChicagoTime)
        {
                // DEFENSIVE: Validate bar data before buffering
                string? validationError = null;
                if (high < low)
                {
                    validationError = "high < low";
                }
                else if (close < low || close > high)
                {
                    validationError = "close outside [low, high]";
                }
                
                if (validationError != null)
                {
                    // C) Data feed anomaly: Invalid bar data (WARN level)
                    LogHealth("WARN", "DATA_FEED_INVALID_BAR", $"Invalid bar data: {validationError}",
                        new
                        {
                            bar_utc = barUtc.ToString("o"),
                            bar_chicago = barChicagoTime.ToString("o"),
                            high = high,
                            low = low,
                            close = close,
                            validation_error = validationError
                        });
                    
                    // Log invalid bar but continue (fail-closed per bar, not per stream)
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_INVALID", State.ToString(),
                        new
                        {
                            instrument = Instrument,
                            bar_utc_time = barUtc.ToString("o"),
                            bar_chicago_time = barChicagoTime.ToString("o"),
                            high = high,
                            low = low,
                            close = close,
                            reason = validationError
                        }));
                    // Skip invalid bar - do not add to buffer
                    return;
                }
                
                // RANGE_FIRST_BAR_ACCEPTED: Emit once per stream per day when first bar enters range
                if (!_firstBarAcceptedAssertEmitted)
                {
                    _firstBarAcceptedAssertEmitted = true;
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "RANGE_FIRST_BAR_ACCEPTED", State.ToString(),
                        new
                        {
                            bar_utc_time = barUtc.ToString("o"),
                            bar_chicago_time = barChicagoTime.ToString("o"),
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            comparison_result = "bar >= range_start",
                            note = "first accepted bar"
                        }));
                }
                
                // Log if buffering in unexpected state (rate-limited)
                var isExpectedState = State == StreamState.PRE_HYDRATION || State == StreamState.ARMED || State == StreamState.RANGE_BUILDING;
                
                // BINARY TRUTH EVENT: Prove admission-to-commit decision point
                var commitReason = isExpectedState ? "COMMIT_ALLOWED" : $"STATE_GUARD_{State}";
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BAR_ADMISSION_TO_COMMIT_DECISION", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        state = State.ToString(),
                        admitted = true,  // bar passed admission check (we're here)
                        will_commit = isExpectedState,  // state allows buffering
                        reason = commitReason,
                        bar_time_chicago = barChicagoTime.ToString("o"),
                        range_start = RangeStartChicagoTime.ToString("o"),
                        slot_time = SlotTimeChicagoTime.ToString("o")
                    }));
                
                if (!isExpectedState)
                {
                    // Rate-limit warning to once per stream per 5 minutes
                    var shouldLogWarning = !_lastBarBufferedStateIndependentUtc.HasValue || 
                        (utcNow - _lastBarBufferedStateIndependentUtc.Value).TotalMinutes >= 5.0;
                    
                    if (shouldLogWarning)
                    {
                        _lastBarBufferedStateIndependentUtc = utcNow;
                        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "BAR_BUFFERED_STATE_INDEPENDENT", State.ToString(),
                            new
                            {
                                stream_state = State.ToString(),
                                bar_count = GetBarBufferCount(),
                                bar_chicago = barChicagoTime.ToString("o"),
                                note = "Bar buffered in unexpected state - state-independent buffering active"
                            }));
                    }
                }
                
                // Add bar to buffer with actual open price
                // Determine bar source: LIVE if from live feed, BARSREQUEST if marked as historical from BarsRequest
                var barSource = isHistorical ? BarSource.BARSREQUEST : BarSource.LIVE;
                AddBarToBuffer(new Bar(barUtc, open, high, low, close, null), barSource);
                
                // Gap tolerance tracking (treat bar timestamp as bar OPEN time in Chicago)
                if (_lastBarOpenChicago.HasValue)
                {
                    // IMPORTANT:
                    // - For 1-minute bars, a "normal" delta is ~1 minute.
                    // - A delta of 2 minutes means we are missing ~1 minute-bar (2 - 1).
                    // We track missing minutes (delta - 1), not the raw delta, otherwise totals explode too fast.
                    var gapDeltaMinutes = (barChicagoTime - _lastBarOpenChicago.Value).TotalMinutes;
                    
                    // Only track gaps > 1 minute (normal 1-minute bars have ~1 minute gaps)
                    if (gapDeltaMinutes > 1.0)
                    {
                        var missingMinutes = gapDeltaMinutes - 1.0;

                        // Update gap tracking
                        if (missingMinutes > _largestSingleGapMinutes)
                            _largestSingleGapMinutes = missingMinutes;
                        
                        var totalGapBefore = _totalGapMinutes;
                        _totalGapMinutes += missingMinutes;
                        var totalGapAfter = _totalGapMinutes;
                        var largestGapAfter = _largestSingleGapMinutes;
                        
                        // BAR_GAP_DETECTED: Diagnostic event to instantly identify gap root causes
                        // This helps determine if gaps are real missing minutes, time mapping errors,
                        // out-of-order bars, or filtering issues
                        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "BAR_GAP_DETECTED", State.ToString(),
                            new
                            {
                                stream_id = Stream,
                                prev_bar_open_chicago = _lastBarOpenChicago.Value.ToString("o"),
                                this_bar_open_chicago = barChicagoTime.ToString("o"),
                                delta_minutes = gapDeltaMinutes,
                                added_to_total_gap = missingMinutes,
                                total_gap_now = totalGapAfter,
                                largest_gap_now = largestGapAfter,
                                bar_source = barSource.ToString(),
                                // Additional diagnostic context
                                stream_state = State.ToString(),
                                bar_timestamp_utc = barUtc.ToString("o"),
                                gap_type_preliminary = State == StreamState.PRE_HYDRATION || barSource == BarSource.BARSREQUEST ? "DATA_FEED_FAILURE" : "LOW_LIQUIDITY",
                                note = "Gap detected between consecutive bars. Check prev_bar_open_chicago vs this_bar_open_chicago to identify root cause."
                            }));
                        
                        // Classify gap type: DATA_FEED_FAILURE vs LOW_LIQUIDITY
                        // DATA_FEED_FAILURE indicators:
                        // - Gaps during PRE_HYDRATION (BARSREQUEST should return complete data)
                        // - Very low bar count overall (suggests data feed issue)
                        // - Gaps from BARSREQUEST source (historical data should be complete)
                        // LOW_LIQUIDITY indicators:
                        // - Gaps during RANGE_BUILDING from LIVE feed (market genuinely sparse)
                        // - Reasonable bar count but with gaps (some trading occurred)
                        var totalBarCount = _historicalBarCount + _liveBarCount;
                        var expectedMinBars = (int)((SlotTimeChicagoTime - RangeStartChicagoTime).TotalMinutes * 0.5); // Expect at least 50% coverage
                        var isDataFeedFailure = 
                            State == StreamState.PRE_HYDRATION || // PRE_HYDRATION gaps = data feed issue
                            barSource == BarSource.BARSREQUEST || // Historical gaps = data feed issue
                            totalBarCount < expectedMinBars; // Very low bar count = data feed issue
                        
                        var gapType = isDataFeedFailure ? "DATA_FEED_FAILURE" : "LOW_LIQUIDITY";
                        var gapTypeNote = isDataFeedFailure 
                            ? "Gap likely due to data feed failure (PRE_HYDRATION/BARSREQUEST gaps or insufficient data)"
                            : "Gap likely due to legitimate low liquidity (sparse trading during live feed)";
                        
                        // Check gap tolerance rules - DISABLED: DATA_FEED_FAILURE gaps no longer invalidate
                        // Both DATA_FEED_FAILURE and LOW_LIQUIDITY gaps are now tolerated (never invalidate)
                        bool violated = false;
                        string violationReason = "";
                        
                        // TEMPORARILY DISABLED: DATA_FEED_FAILURE gap invalidation
                        // Previously, DATA_FEED_FAILURE gaps would invalidate ranges, but this is now disabled
                        // All gaps (both DATA_FEED_FAILURE and LOW_LIQUIDITY) are tolerated and logged for monitoring
                        // if (isDataFeedFailure)
                        // {
                        //     if (missingMinutes > MAX_SINGLE_GAP_MINUTES)
                        //     {
                        //         violated = true;
                        //         violationReason = $"Single gap missing {missingMinutes:F1} minutes exceeds MAX_SINGLE_GAP_MINUTES ({MAX_SINGLE_GAP_MINUTES}) for DATA_FEED_FAILURE";
                        //     }
                        //     else if (_totalGapMinutes > MAX_TOTAL_GAP_MINUTES)
                        //     {
                        //         violated = true;
                        //         violationReason = $"Total gap missing {_totalGapMinutes:F1} minutes exceeds MAX_TOTAL_GAP_MINUTES ({MAX_TOTAL_GAP_MINUTES}) for DATA_FEED_FAILURE";
                        //     }
                        //     
                        //     // Check last 10 minutes rule
                        //     var last10MinStart = SlotTimeChicagoTime.AddMinutes(-10);
                        //     if (barChicagoTime >= last10MinStart && missingMinutes > MAX_GAP_LAST_10_MINUTES)
                        //     {
                        //         violated = true;
                        //         violationReason = $"Gap missing {missingMinutes:F1} minutes in last 10 minutes exceeds MAX_GAP_LAST_10_MINUTES ({MAX_GAP_LAST_10_MINUTES}) for DATA_FEED_FAILURE";
                        //     }
                        // }
                        // LOW_LIQUIDITY gaps: Never invalidate (violated stays false)
                        // DATA_FEED_FAILURE gaps: Also never invalidate (violated stays false) - TEMPORARILY DISABLED
                        
                        if (violated)
                        {
                            var wasInvalidated = _rangeInvalidated;
                            _rangeInvalidated = true;
                            
                            var gapViolationData = new
                            {
                                instrument = Instrument,  // Canonical (top-level for backward compatibility)
                                execution_instrument = ExecutionInstrument,  // PHASE 3: Execution identity
                                canonical_instrument = CanonicalInstrument,   // PHASE 3: Canonical identity
                                slot = Stream,
                                violation_reason = violationReason,
                                gap_type = gapType,
                                gap_type_note = gapTypeNote,
                                // Backward-compat: keep gap_minutes, but now it means "missing minutes"
                                gap_minutes = missingMinutes,
                                // Extra forensic context: raw delta between bar opens
                                gap_delta_minutes = gapDeltaMinutes,
                                largest_single_gap_minutes = _largestSingleGapMinutes,
                                total_gap_minutes = _totalGapMinutes,
                                previous_bar_open_chicago = _lastBarOpenChicago.Value.ToString("o"),
                                current_bar_open_chicago = barChicagoTime.ToString("o"),
                                slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                                gap_location = $"Between {_lastBarOpenChicago.Value:HH:mm} and {barChicagoTime:HH:mm} Chicago time",
                                minutes_until_slot_time = (SlotTimeChicagoTime - barChicagoTime).TotalMinutes,
                                // Diagnostic context for gap classification
                                stream_state = State.ToString(),
                                bar_source = barSource.ToString(),
                                total_bar_count = totalBarCount,
                                historical_bar_count = _historicalBarCount,
                                live_bar_count = _liveBarCount,
                                expected_min_bars = expectedMinBars,
                                range_start_chicago = RangeStartChicagoTime.ToString("o"),
                                note = isDataFeedFailure
                                    ? (missingMinutes > MAX_SINGLE_GAP_MINUTES 
                                        ? $"Single gap missing {missingMinutes:F1} minutes exceeds limit of {MAX_SINGLE_GAP_MINUTES} minutes for DATA_FEED_FAILURE"
                                        : _totalGapMinutes > MAX_TOTAL_GAP_MINUTES
                                        ? $"Total gaps missing {_totalGapMinutes:F1} minutes exceed limit of {MAX_TOTAL_GAP_MINUTES} minutes for DATA_FEED_FAILURE"
                                        : $"Gap missing {missingMinutes:F1} minutes in last 10 minutes exceeds limit of {MAX_GAP_LAST_10_MINUTES} minutes for DATA_FEED_FAILURE")
                                    : $"LOW_LIQUIDITY gap tolerated (gaps from low liquidity never invalidate range)"
                            };
                            
                            // Log to health directory (detailed health tracking)
                            LogHealth("ERROR", "GAP_TOLERANCE_VIOLATION", $"Range invalidated due to gap violation: {violationReason}", gapViolationData);
                            
                            // Also log to main engine log for easier discovery
                            _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                                "GAP_TOLERANCE_VIOLATION", State.ToString(), gapViolationData));
                            
                            // Notify RANGE_INVALIDATED once per slot (transition false → true)
                            if (!wasInvalidated && !_rangeInvalidatedNotified)
                            {
                                _rangeInvalidatedNotified = true;
                                var notificationKey = $"RANGE_INVALIDATED:{Stream}";
                                var title = $"Range Invalidated: {Instrument} {Stream}";
                                var message = $"Range invalidated due to gap violation: {violationReason}. " +
                                           $"Gap type: {gapType} - {gapTypeNote}. Trading blocked for this slot.";
                                _alertCallback?.Invoke(notificationKey, title, message, 1); // High priority
                            }
                        }
                        else
                        {
                            // Gap within tolerance - log as WARN with gap type classification
                            LogHealth("WARN", "GAP_TOLERATED", $"Gap missing {missingMinutes:F1} minutes tolerated (within limits for {gapType})",
                                new
                                {
                                    instrument = Instrument,
                                    slot = Stream,
                                    gap_type = gapType,
                                    gap_type_note = gapTypeNote,
                                    gap_minutes = missingMinutes,
                                    gap_delta_minutes = gapDeltaMinutes,
                                    largest_single_gap_minutes = _largestSingleGapMinutes,
                                    total_gap_minutes = _totalGapMinutes,
                                    previous_bar_open_chicago = _lastBarOpenChicago.Value.ToString("o"),
                                    current_bar_open_chicago = barChicagoTime.ToString("o"),
                                    stream_state = State.ToString(),
                                    bar_source = barSource.ToString()
                                });
                        }
                    }
                }
                
                // Update last bar open time (Chicago, bar OPEN time)
                _lastBarOpenChicago = barChicagoTime;
                
                // SPECULATIVE UPDATE — MUST NOT RUN AFTER RANGE_LOCKED
                // Keep incremental updates only while State == RANGE_BUILDING AND _rangeLocked == false
                // After _rangeLocked == true, OnBar must not modify RangeHigh/RangeLow/FreezeClose/FreezeCloseSource
                // This is non-negotiable. Without it, late bars can mutate a locked range.
                if (State == StreamState.RANGE_BUILDING && !_rangeLocked)
                {
                    // INCREMENTAL UPDATE: Update RangeHigh/RangeLow as bars arrive
                    // This allows range to update in real-time instead of only at slot_time
                    if (RangeHigh == null || high > RangeHigh.Value)
                        RangeHigh = high;
                    if (RangeLow == null || low < RangeLow.Value)
                        RangeLow = low;
                    // Always update FreezeClose to latest bar's close (will be last bar before slot_time)
                    FreezeClose = close;
                    FreezeCloseSource = "BAR_CLOSE";
                    // Persist snapshot for restart recovery (whenever range or bar count changes)
                    PersistRangeBuildingSnapshot(barUtc);
                }
        }
        else
        {
            // DIAGNOSTIC: Log bars that are filtered out (rate-limited, only when close to window and diagnostics enabled)
            if (_enableDiagnosticLogs && shouldLogBar)
            {
                var timeUntilStart = (RangeStartChicagoTime - barChicagoTime).TotalMinutes;
                var timeAfterEnd = (barChicagoTime - SlotTimeChicagoTime).TotalMinutes;
                if (Math.Abs(timeUntilStart) < 30 || Math.Abs(timeAfterEnd) < 30)
                {
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_FILTERED_OUT", State.ToString(),
                        new
                        {
                            bar_utc = barUtc.ToString("o"),
                            bar_chicago = barChicagoTime.ToString("o"),
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            range_end_chicago = SlotTimeChicagoTime.ToString("o"),
                            reason = barChicagoTime < RangeStartChicagoTime ? "BEFORE_RANGE_START" : "AFTER_RANGE_END",
                            minutes_from_start = timeUntilStart,
                            minutes_from_end = timeAfterEnd
                        }));
                }
            }
        }
        // Bars at/after slot time are for breakout detection (handled in RANGE_LOCKED state)
        
        // Handle RANGE_LOCKED state separately (bars at/after slot time for breakout detection)
        if (State == StreamState.RANGE_LOCKED)
        {
            ApplyPostLockBreakoutExcursionFromBar(high, low, barUtc, utcNow);
            if (_journal.Committed)
                return;

            // DIAGNOSTIC: Log execution gate evaluation (rate-limited, only if diagnostics enabled)
            if (_enableDiagnosticLogs)
            {
                var barChicago = _time.ConvertUtcToChicago(barUtc);
                var timeSinceLastEval = (barUtc - _lastExecutionGateEvalBarUtc).TotalSeconds;
                if (timeSinceLastEval >= EXECUTION_GATE_EVAL_RATE_LIMIT_SECONDS || _lastExecutionGateEvalBarUtc == DateTimeOffset.MinValue)
                {
                    _lastExecutionGateEvalBarUtc = barUtc;
                    LogExecutionGateEval(barUtc, barChicago, utcNow);
                }
            }

            // SIMPLIFICATION: Removed CheckBreakoutEntry() - stop brackets handle breakouts automatically
            // Stop brackets (OCO-linked Long + Short) are submitted at lock and fill automatically when price hits breakout level
            // This eliminates race conditions and simplifies execution to 2 paths: immediate entry OR stop brackets
            // Breakout detection on bars is no longer needed - stop brackets handle it
            // 
            // OLD CODE (removed):
            // if (!_entryDetected && barUtc >= SlotTimeUtc && barUtc < MarketCloseUtc && _brkLongRounded.HasValue && _brkShortRounded.HasValue)
            // {
            //     CheckBreakoutEntry(barUtc, high, low, utcNow);
            // }
        }
    }

    /// <summary>
    /// Diagnostic: Log execution gate evaluation to identify which gate is blocking execution.
    /// </summary>
    private void LogExecutionGateEval(DateTimeOffset barUtc, DateTimeOffset barChicago, DateTimeOffset utcNow)
    {
        var barChicagoTime = barChicago.ToString("HH:mm:ss");
        var slotTimeUtcParsed = SlotTimeUtc;
        var slotReached = barUtc >= slotTimeUtcParsed;
        var slotTimeChicagoStr = SlotTimeChicago ?? "";
        
        // Evaluate all gates
        var realtimeOk = true; // Assume realtime if we're getting bars
        var tradingDay = TradingDate ?? "";
        var session = Session ?? "";
        var sessionActive = !string.IsNullOrEmpty(session) && _spec.sessions.ContainsKey(session);
        var timetableEnabled = true; // Timetable is validated at engine level
        var streamArmed = !_journal.Committed && State != StreamState.DONE;
        var stateOk = State == StreamState.RANGE_LOCKED;
        var entryDetectionModeOk = true; // FIXED: Now works for all modes
        
        // Check if we can detect entries
        var canDetectEntries = stateOk && !_entryDetected && slotReached && 
                               barUtc < MarketCloseUtc && 
                               _brkLongRounded.HasValue && _brkShortRounded.HasValue;
        
        // Final allowed: all gates must pass (execution mode is adapter-only, not a gate)
        var finalAllowed = realtimeOk && 
                          !string.IsNullOrEmpty(tradingDay) &&
                          sessionActive &&
                          slotReached &&
                          timetableEnabled &&
                          streamArmed &&
                          stateOk &&
                          entryDetectionModeOk &&
                          canDetectEntries;

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "EXECUTION_GATE_EVAL", State.ToString(),
            new
            {
                bar_timestamp_chicago = barChicagoTime,
                bar_timestamp_utc = barUtc.ToString("o"),
                slot_time_chicago = slotTimeChicagoStr,
                slot_time_utc = slotTimeUtcParsed.ToString("o"),
                realtime_ok = realtimeOk,
                trading_day = tradingDay,
                session = session,
                session_active = sessionActive,
                slot_reached = slotReached,
                timetable_enabled = timetableEnabled,
                stream_armed = streamArmed,
                state_ok = stateOk,
                state = State.ToString(),
                entry_detection_mode_ok = entryDetectionModeOk,
                execution_mode = _executionMode.ToString(),
                can_detect_entries = canDetectEntries,
                entry_detected = _entryDetected,
                breakout_levels_computed = _brkLongRounded.HasValue && _brkShortRounded.HasValue,
                final_allowed = finalAllowed
            }));

        // INVARIANT CHECK: If slot time has passed and execution should be allowed but isn't, log ERROR
        // Only trigger violation if execution is blocked for UNEXPECTED reasons (not legitimate blocks)
        // Estimate bar interval (typically 1 minute for NG)
        var estimatedBarIntervalMinutes = 1;
        var barInterval = TimeSpan.FromMinutes(estimatedBarIntervalMinutes);
        var slotTimePlusInterval = slotTimeUtcParsed.Add(barInterval);
        
        // Determine which gates are failing
        var failedGates = new List<string>();
        if (!realtimeOk) failedGates.Add("REALTIME_OK");
        if (string.IsNullOrEmpty(tradingDay)) failedGates.Add("TRADING_DAY_SET");
        if (!sessionActive) failedGates.Add("SESSION_ACTIVE");
        if (!slotReached) failedGates.Add("SLOT_REACHED");
        if (!timetableEnabled) failedGates.Add("TIMETABLE_ENABLED");
        if (!streamArmed) failedGates.Add("STREAM_ARMED");
        if (!stateOk) failedGates.Add("STATE_OK");
        if (!entryDetectionModeOk) failedGates.Add("ENTRY_DETECTION_MODE_OK");
        if (!canDetectEntries) failedGates.Add("CAN_DETECT_ENTRIES");
        
        // Only violate if execution is blocked for UNEXPECTED reasons:
        // - State is OK (RANGE_LOCKED)
        // - Slot has been reached
        // - Stream is armed (not committed/done)
        // - Entry not detected yet (still waiting for entry)
        // - Breakout levels are computed (ready to detect entries)
        // This excludes legitimate blocks: entry already detected, journal committed, breakout levels not ready
        var unexpectedBlock = barUtc >= slotTimePlusInterval && 
                             !finalAllowed && 
                             stateOk && 
                             slotReached && 
                             streamArmed &&  // Stream should be armed
                             !_entryDetected &&  // Entry not detected yet
                             _brkLongRounded.HasValue && _brkShortRounded.HasValue;  // Breakout levels ready
        
        if (unexpectedBlock)
        {
            // This is a real violation - execution should be allowed but isn't
            var payload = new Dictionary<string, object>
            {
                ["error"] = "EXECUTION_SHOULD_BE_ALLOWED_BUT_IS_NOT",
                ["bar_timestamp_chicago"] = barChicagoTime,
                ["slot_time_chicago"] = slotTimeChicagoStr,
                ["slot_time_utc"] = slotTimeUtcParsed.ToString("o"),
                ["bar_interval_minutes"] = estimatedBarIntervalMinutes,
                ["realtime_ok"] = realtimeOk,
                ["trading_day"] = tradingDay,
                ["session_active"] = sessionActive,
                ["slot_reached"] = slotReached,
                ["timetable_enabled"] = timetableEnabled,
                ["stream_armed"] = streamArmed,
                ["can_detect_entries"] = canDetectEntries,
                ["entry_detected"] = _entryDetected,
                ["breakout_levels_computed"] = _brkLongRounded.HasValue && _brkShortRounded.HasValue,
                ["execution_mode"] = _executionMode.ToString(),
                ["instrument"] = Instrument,
                ["stream"] = Stream,
                ["trading_date"] = TradingDate,
                ["state"] = State.ToString(),
                ["failed_gates"] = string.Join(", ", failedGates),  // List of gates that failed
                ["message"] = $"Slot time has passed but execution is not allowed. Failed gates: {string.Join(", ", failedGates)}"
            };
            
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "EXECUTION_GATE_INVARIANT_VIOLATION", State.ToString(),
                payload));
            
            // Report critical event to HealthMonitor for notification
            _reportCriticalCallback?.Invoke("EXECUTION_GATE_INVARIANT_VIOLATION", payload, TradingDate);
        }
    }

    private void ComputeBreakoutLevelsAndLog(DateTimeOffset utcNow)
    {
        if (RangeHigh is null || RangeLow is null)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "BREAKOUT_LEVELS_COMPUTED", State.ToString(),
                new { error = "MISSING_RANGE_VALUES", rounding_required = true }));
            return;
        }

        // Compute raw breakout levels
        _brkLongRaw = RangeHigh.Value + _tickSize;
        _brkShortRaw = RangeLow.Value - _tickSize;

        // Round using Analyzer-equivalent method (ALL execution modes need rounded levels)
        _brkLongRounded = UtilityRoundToTick.RoundToTick(_brkLongRaw.Value, _tickSize);
        _brkShortRounded = UtilityRoundToTick.RoundToTick(_brkShortRaw.Value, _tickSize);

        // Log breakout levels (all modes - was DRYRUN-only, now unconditional for consistency)
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "BREAKOUT_LEVELS_COMPUTED", State.ToString(),
            new
            {
                brk_long_raw = _brkLongRaw,
                brk_short_raw = _brkShortRaw,
                brk_long_rounded = _brkLongRounded,
                brk_short_rounded = _brkShortRounded,
                tick_size = _tickSize,
                rounding_method = _spec.breakout.tick_rounding.method
            }));
    }

    private void LogIntendedBracketsPlaced(DateTimeOffset utcNow)
    {
        BracketsIntended = true;
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "INTENDED_BRACKETS_PLACED", State.ToString(),
            new { brackets_intended = true, note = "Brackets intended. In SIM/LIVE, stop-entry brackets may be submitted at RANGE_LOCKED." }));
    }

    /// <summary>
    /// Consolidated precondition check for stop bracket submission.
    /// Returns a tuple indicating whether submission can proceed and the reason if not.
    /// </summary>
    private (bool CanSubmit, string Reason, object? Details) CanSubmitStopBrackets(DateTimeOffset utcNow)
    {
        // Idempotency: only once per stream per day
        if (_stopBracketsSubmittedAtLock) 
        { 
            return (false, "IDEMPOTENCY", new { _stopBracketsSubmittedAtLock = true }); 
        }
        
        // Preconditions
        if (_journal.Committed || State == StreamState.DONE) 
        { 
            return (false, "JOURNAL_COMMITTED_OR_DONE", new { journal_committed = _journal?.Committed ?? false, state = State.ToString() }); 
        }
        
        if (_rangeInvalidated) 
        { 
            return (false, "RANGE_INVALIDATED", new { _rangeInvalidated = true }); 
        }
        
        if (_breakoutLevelsMissing) 
        { 
            return (false, "BREAKOUT_LEVELS_MISSING", new { _breakoutLevelsMissing = true, note = "Stream gated from entry until breakout levels are computed" }); 
        }
        
        if (_executionAdapter == null || _executionJournal == null || _riskGate == null) 
        { 
            return (false, "NULL_DEPENDENCIES", new { execution_adapter_null = _executionAdapter == null, execution_journal_null = _executionJournal == null, risk_gate_null = _riskGate == null }); 
        }

        if (!_executionAdapter.IsExecutionContextReady)
        {
            return (false, "EXECUTION_CONTEXT_NOT_READY", new
            {
                note = "NT context not wired or SIM not verified — bracket submission deferred (startup/readiness contract)"
            });
        }
        
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue) 
        { 
            return (false, "BREAKOUT_LEVELS_MISSING", new { brk_long_has_value = _brkLongRounded.HasValue, brk_short_has_value = _brkShortRounded.HasValue }); 
        }
        
        if (!RangeHigh.HasValue || !RangeLow.HasValue) 
        { 
            return (false, "RANGE_VALUES_MISSING", new { range_high_has_value = RangeHigh.HasValue, range_low_has_value = RangeLow.HasValue }); 
        }

        if (IsPostLockBreakoutSetupExpired())
        {
            return (false, "POST_LOCK_BREAKOUT_ALREADY_OCCURRED", new
            {
                post_lock_long = _journal.PostLockLongBreakoutTouched,
                post_lock_short = _journal.PostLockShortBreakoutTouched,
                breakout_source = "JOURNAL_FLAGS"
            });
        }

        var (ohlcL, ohlcS) = GetPostSlotBreakoutTouchFromOhlcEnvelope(_brkLongRounded.Value, _brkShortRounded.Value);
        if (ohlcL || ohlcS)
        {
            return (false, "POST_LOCK_BREAKOUT_ALREADY_OCCURRED", new
            {
                post_lock_long = ohlcL || _journal.PostLockLongBreakoutTouched,
                post_lock_short = ohlcS || _journal.PostLockShortBreakoutTouched,
                ohlc_long = ohlcL,
                ohlc_short = ohlcS,
                max_high_since_slot = _postSlotExcursionHasSamples ? _postSlotMaxHighSinceSlot : (decimal?)null,
                min_low_since_slot = _postSlotExcursionHasSamples ? _postSlotMinLowSinceSlot : (decimal?)null,
                breakout_source = "OHLC_ENVELOPE"
            });
        }
        
        return (true, "OK", null);
    }

    /// <summary>
    /// True after RANGE_LOCK if bar path shows either breakout was touched before entry brackets were armed.
    /// Strict product rule: touching either side expires the entire setup (no stop-entry brackets on either side).
    /// </summary>
    public bool IsPostLockBreakoutSetupExpired()
        => _journal.PostLockLongBreakoutTouched || _journal.PostLockShortBreakoutTouched;

    /// <summary>
    /// Primary signal for &quot;breakout already occurred&quot; before first bracket submit: tracked OHLC strictly after SlotTimeUtc.
    /// </summary>
    private (bool longTouched, bool shortTouched) GetPostSlotBreakoutTouchFromOhlcEnvelope(decimal brkLong, decimal brkShort)
    {
        if (!_postSlotExcursionHasSamples) return (false, false);
        return (_postSlotMaxHighSinceSlot >= brkLong, _postSlotMinLowSinceSlot <= brkShort);
    }

    /// <summary>
    /// If OHLC since slot or persisted journal shows post-lock excursion, commit NO_TRADE and return true.
    /// Call before initial SubmitStopEntryBracketsAtLock from TryLockRange Phase B.
    /// </summary>
    private bool TryCommitNoTradeIfPostLockBreakoutDetected(DateTimeOffset utcNow, string detectionPath)
    {
        if (_journal.Committed) return false;
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue) return false;

        var brkL = _brkLongRounded.Value;
        var brkS = _brkShortRounded.Value;
        var (ohlcL, ohlcS) = GetPostSlotBreakoutTouchFromOhlcEnvelope(brkL, brkS);
        var journalL = _journal.PostLockLongBreakoutTouched;
        var journalS = _journal.PostLockShortBreakoutTouched;
        if (!ohlcL && !ohlcS && !journalL && !journalS)
            return false;

        if (ohlcL) _journal.PostLockLongBreakoutTouched = true;
        if (ohlcS) _journal.PostLockShortBreakoutTouched = true;
        _journals.Save(_journal);

        var source = (ohlcL || ohlcS) && (journalL || journalS)
            ? "OHLC_AND_JOURNAL"
            : (ohlcL || ohlcS ? "OHLC_ENVELOPE" : "JOURNAL_FLAGS_ONLY");

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "INITIAL_SUBMISSION_BLOCKED_POST_LOCK_EXCURSION", State.ToString(),
            new
            {
                stream_id = Stream,
                trading_date = TradingDate,
                detection_path = detectionPath,
                source,
                ohlc_long = ohlcL,
                ohlc_short = ohlcS,
                post_lock_long = _journal.PostLockLongBreakoutTouched,
                post_lock_short = _journal.PostLockShortBreakoutTouched,
                max_high_since_slot = _postSlotExcursionHasSamples ? _postSlotMaxHighSinceSlot : (decimal?)null,
                min_low_since_slot = _postSlotExcursionHasSamples ? _postSlotMinLowSinceSlot : (decimal?)null,
                breakout_long = brkL,
                breakout_short = brkS,
                note = "Post-slot OHLC envelope or journal memory — no stop-entry brackets (strict product rule)"
            }));

        _ = Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED");
        return true;
    }

    private void SyncJournalPostLockFlagsFromOhlcEnvelopeIfTouched()
    {
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue) return;
        var (l, s) = GetPostSlotBreakoutTouchFromOhlcEnvelope(_brkLongRounded.Value, _brkShortRounded.Value);
        if (!l && !s) return;
        if (l) _journal.PostLockLongBreakoutTouched = true;
        if (s) _journal.PostLockShortBreakoutTouched = true;
        _journals.Save(_journal);
    }

    private static bool IsEntryStopMarketMovedRejection(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        return error.IndexOf("can't be placed", StringComparison.OrdinalIgnoreCase) >= 0 ||
               error.IndexOf("cannot be placed", StringComparison.OrdinalIgnoreCase) >= 0 ||
               error.IndexOf("price outside limits", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void MarkPostLockBreakoutFromRejectedSide(bool longRejected, bool shortRejected)
    {
        if (longRejected) _journal.PostLockLongBreakoutTouched = true;
        if (shortRejected) _journal.PostLockShortBreakoutTouched = true;
        _journals.Save(_journal);
    }

    /// <summary>
    /// Deterministic post-lock excursion: bar must be strictly after slot end; uses bar high/low vs rounded breakout stops.
    /// On first touch, persists journal flags, emits audit event, and commits NO_TRADE_BREAKOUT_ALREADY_OCCURRED.
    /// </summary>
    private void ApplyPostLockBreakoutExcursionFromBar(decimal high, decimal low, DateTimeOffset barUtc, DateTimeOffset utcNow)
    {
        if (!_rangeLocked || State != StreamState.RANGE_LOCKED) return;
        if (_journal.Committed) return;
        if (_stopBracketsSubmittedAtLock || _journal.StopBracketsSubmittedAtLock) return;
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue) return;
        if (barUtc <= SlotTimeUtc) return;

        var brkL = _brkLongRounded.Value;
        var brkS = _brkShortRounded.Value;
        var longTouch = high >= brkL;
        var shortTouch = low <= brkS;
        if (!longTouch && !shortTouch) return;

        var priorL = _journal.PostLockLongBreakoutTouched;
        var priorS = _journal.PostLockShortBreakoutTouched;
        if (longTouch) _journal.PostLockLongBreakoutTouched = true;
        if (shortTouch) _journal.PostLockShortBreakoutTouched = true;
        _journals.Save(_journal);

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "ENTRY_INVALIDATED_POST_LOCK_EXCURSION", State.ToString(),
            new
            {
                stream_id = Stream,
                trading_date = TradingDate,
                bar_utc = barUtc.ToString("o"),
                breakout_long = brkL,
                breakout_short = brkS,
                bar_high = high,
                bar_low = low,
                long_touched = longTouch || priorL,
                short_touched = shortTouch || priorS,
                note = "Breakout touched on post-lock bar path before entry brackets armed — setup expired (strict)"
            }));

        if (!Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED")) return;
    }

    /// <summary>
    /// Restart safety: journal persisted excursion flags but commit did not complete (e.g. crash). Finish NO_TRADE.
    /// </summary>
    private void EnsureCommittedForPostLockExcursion(DateTimeOffset utcNow)
    {
        if (_journal.Committed || State != StreamState.RANGE_LOCKED) return;
        if (!IsPostLockBreakoutSetupExpired()) return;
        if (!Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED")) return;
    }

    private DerivedPositionAuthority TryDerivedPositionAuthorityForStream(DateTimeOffset utcNow)
    {
        if (_executionAdapter == null || _executionJournal == null)
            return DerivedPositionAuthority.UNKNOWN;
        try
        {
            var snap = _executionAdapter.GetAccountSnapshot(utcNow);
            return PositionAuthorityInstrumentEvaluator.Derive(snap, _executionJournal, ExecutionInstrument, CanonicalInstrument);
        }
        catch
        {
            return DerivedPositionAuthority.UNKNOWN;
        }
    }

    private DateTimeOffset ResolveDeferredBracketExpiryUtc(DateTimeOffset utcNow)
    {
        if (_journal.NextSlotTimeUtc.HasValue && _journal.NextSlotTimeUtc.Value > utcNow)
            return _journal.NextSlotTimeUtc.Value;
        if (MarketCloseUtc > utcNow)
            return MarketCloseUtc;
        return utcNow.AddMinutes(15);
    }

    private void ClearDeferredBracketTrade()
    {
        _deferredBracketTradePending = false;
        _deferredBracketTradeExpiryUtc = null;
        _loggedTradeExecutedFromDeferred = false;
    }

    private void TryProcessDeferredBracketTradeAuthority(DateTimeOffset utcNow)
    {
        if (!_deferredBracketTradePending) return;
        if (State != StreamState.RANGE_LOCKED || _journal.Committed)
        {
            ClearDeferredBracketTrade();
            return;
        }

        if (_deferredBracketTradeExpiryUtc.HasValue && utcNow > _deferredBracketTradeExpiryUtc.Value)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "TRADE_EXPIRED", State.ToString(),
                new
                {
                    stream_id = Stream,
                    trading_date = TradingDate,
                    execution_instrument = ExecutionInstrument,
                    authority_deferred_expiry_utc = _deferredBracketTradeExpiryUtc.Value.ToString("o"),
                    note = "Deferred stop-entry bracket submit expired before REAL authority"
                }));
            ClearDeferredBracketTrade();
            return;
        }

        var auth = TryDerivedPositionAuthorityForStream(utcNow);
        if (auth == DerivedPositionAuthority.UNKNOWN)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "TRADE_CANCELLED_UNKNOWN_STATE", State.ToString(),
                new
                {
                    stream_id = Stream,
                    trading_date = TradingDate,
                    execution_instrument = ExecutionInstrument,
                    authority_state = auth.ToString(),
                    reason = "authority_unknown",
                    note = "Deferred bracket submit cancelled — position authority UNKNOWN"
                }));
            ClearDeferredBracketTrade();
            return;
        }

        if (auth != DerivedPositionAuthority.REAL)
            return;

        if (!_loggedTradeExecutedFromDeferred)
        {
            _loggedTradeExecutedFromDeferred = true;
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "TRADE_EXECUTED_FROM_DEFERRED", State.ToString(),
                new
                {
                    stream_id = Stream,
                    trading_date = TradingDate,
                    execution_instrument = ExecutionInstrument,
                    authority_state = auth.ToString(),
                    note = "Executing deferred stop-entry brackets now that authority is REAL"
                }));
        }

        SubmitStopEntryBracketsAtLock(utcNow, fromDeferredAuthorityExecution: true);
        if (_stopBracketsSubmittedAtLock || _journal.StopBracketsSubmittedAtLock)
            ClearDeferredBracketTrade();
    }

    /// <summary>
    /// Submit paired stop-market entry orders (long + short) immediately after RANGE_LOCKED.
    /// These are linked via OCO so only one side can fill.
    /// </summary>
    private void SubmitStopEntryBracketsAtLock(DateTimeOffset utcNow, bool fromDeferredAuthorityExecution = false)
    {
        // DIAGNOSTIC: Log entry into function
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "STOP_BRACKETS_SUBMIT_ENTERED", State.ToString(),
            new
            {
                stream_id = Stream,
                trading_date = TradingDate,
                _stopBracketsSubmittedAtLock = _stopBracketsSubmittedAtLock,
                journal_committed = _journal?.Committed ?? false,
                state = State.ToString(),
                range_invalidated = _rangeInvalidated,
                execution_adapter_null = _executionAdapter == null,
                execution_journal_null = _executionJournal == null,
                risk_gate_null = _riskGate == null,
                brk_long_has_value = _brkLongRounded.HasValue,
                brk_short_has_value = _brkShortRounded.HasValue,
                range_high_has_value = RangeHigh.HasValue,
                range_low_has_value = RangeLow.HasValue,
                note = "Entered SubmitStopEntryBracketsAtLock - checking preconditions"
            }));

        // SIMPLIFICATION: Use consolidated precondition check
        var canSubmitResult = CanSubmitStopBrackets(utcNow);
        if (!canSubmitResult.CanSubmit)
        {
            if (canSubmitResult.Reason == "POST_LOCK_BREAKOUT_ALREADY_OCCURRED")
            {
                SyncJournalPostLockFlagsFromOhlcEnvelopeIfTouched();
                _ = Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED");
            }
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_EARLY_RETURN", State.ToString(),
                new { reason = canSubmitResult.Reason, details = canSubmitResult.Details }));
            return;
        }

        // Risk gate (fail-closed)
        bool allowed = false;
        string? reason = null;
        List<string>? failedGates = null;
        var streamArmed = !_journal.Committed && State != StreamState.DONE;
        
        try
        {
            var gateResult = _riskGate.CheckGates(
                _executionMode,
                TradingDate,
                Stream,
                Instrument,
                Session,
                SlotTimeChicago,
                timetableValidated: true,
                streamArmed: streamArmed,
                utcNow);
            allowed = gateResult.Allowed;
            reason = gateResult.Reason;
            failedGates = gateResult.FailedGates;
        }
        catch (Exception ex)
        {
            // CRITICAL: Catch exceptions from risk gate check to prevent crashes
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_EARLY_RETURN", State.ToString(),
                new 
                { 
                    reason = "RISK_GATE_CHECK_EXCEPTION",
                    exception_type = ex.GetType().Name,
                    exception_message = ex.Message,
                    stack_trace = ex.StackTrace,
                    note = "Risk gate check threw exception - blocking order submission to prevent crash"
                }));
            return;
        }

        if (!allowed)
        {
            // Use a deterministic id for bracket attempt logs (not a trade intent id)
            var gateIntentId = $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}";
            try
            {
                _riskGate.LogBlocked(gateIntentId, Instrument, Stream, Session, SlotTimeChicago, TradingDate,
                    reason ?? "UNKNOWN", failedGates ?? new List<string>(), streamArmed, true, utcNow);
            }
            catch (Exception ex)
            {
                // Log that LogBlocked failed but continue
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "STOP_BRACKETS_LOG_BLOCKED_FAILED", State.ToString(),
                    new 
                    { 
                        exception_type = ex.GetType().Name,
                        exception_message = ex.Message,
                        note = "RiskGate.LogBlocked() threw exception - continuing with early return log"
                    }));
            }
            
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_EARLY_RETURN", State.ToString(),
                new 
                { 
                    reason = "RISK_GATE_BLOCKED",
                    risk_gate_reason = reason ?? "UNKNOWN",
                    failed_gates = failedGates ?? new List<string>(),
                    stream_armed = streamArmed
                }));
            return;
        }

        if (!fromDeferredAuthorityExecution)
        {
            var posAuth = TryDerivedPositionAuthorityForStream(utcNow);
            _log.Write(RobotEvents.EngineBase(utcNow, TradingDate, "POSITION_AUTHORITY_EVALUATED", "ENGINE",
                new
                {
                    stream_id = Stream,
                    instrument = ExecutionInstrument,
                    canonical_instrument = CanonicalInstrument,
                    authority_state = posAuth.ToString(),
                    context = "timetable_stop_brackets_at_lock"
                }));
            if (posAuth == DerivedPositionAuthority.UNKNOWN)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "TRADE_BLOCKED_UNKNOWN_STATE", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        trading_date = TradingDate,
                        execution_instrument = ExecutionInstrument,
                        authority_state = posAuth.ToString(),
                        blocked_what = "STOP_ENTRY_BRACKETS",
                        reason = "authority_unknown",
                        note = "No pending trade created — position authority UNKNOWN"
                    }));
                return;
            }

            if (posAuth == DerivedPositionAuthority.RECOVERY)
            {
                if (_deferredBracketTradePending)
                    return;
                _deferredBracketTradePending = true;
                _deferredBracketTradeExpiryUtc = ResolveDeferredBracketExpiryUtc(utcNow);
                _loggedTradeExecutedFromDeferred = false;
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "TRADE_DEFERRED_POSITION_AUTHORITY", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        trading_date = TradingDate,
                        execution_instrument = ExecutionInstrument,
                        authority_state = posAuth.ToString(),
                        deferred_expiry_utc = _deferredBracketTradeExpiryUtc?.ToString("o"),
                        blocked_what = "STOP_ENTRY_BRACKETS",
                        note = "Bracket submit deferred until authority is REAL"
                    }));
                return;
            }
        }

        var brkLong = _brkLongRounded.Value;
        var brkShort = _brkShortRounded.Value;

        // CRITICAL FIX: OCO ID must be unique - NinjaTrader doesn't allow reusing OCO IDs
        // If same stream locks multiple times, previous OCO ID would be reused, causing rejection
        // Add unique identifier (GUID) to ensure each OCO group is unique
        // Shared OCO group links the two entry stops
        var ocoGroup = RobotOrderIds.EncodeEntryOco(TradingDate, Stream, SlotTimeChicago);

        // Compute protective prices deterministically from lock snapshot (pure computation)
        var rh = RangeHigh.Value;
        var rl = RangeLow.Value;
        var (longStop, longTarget, longBeTrigger) = ComputeProtectivesFromLockSnapshot("Long", brkLong, rh, rl);
        var (shortStop, shortTarget, shortBeTrigger) = ComputeProtectivesFromLockSnapshot("Short", brkShort, rh, rl);

        // Build intents (canonical) for idempotency + journaling
        var longIntent = new Intent(
            TradingDate,
            Stream,
            Instrument,
            ExecutionInstrument,
            Session,
            SlotTimeChicago,
            "Long",
            brkLong,
            stopPrice: longStop,
            targetPrice: longTarget,
            beTrigger: longBeTrigger,
            entryTimeUtc: utcNow,
            triggerReason: "ENTRY_STOP_BRACKET_LONG");

        var shortIntent = new Intent(
            TradingDate,
            Stream,
            Instrument,
            ExecutionInstrument,
            Session,
            SlotTimeChicago,
            "Short",
            brkShort,
            stopPrice: shortStop,
            targetPrice: shortTarget,
            beTrigger: shortBeTrigger,
            entryTimeUtc: utcNow,
            triggerReason: "ENTRY_STOP_BRACKET_SHORT");

        var longIntentId = longIntent.ComputeIntentId();
        var shortIntentId = shortIntent.ComputeIntentId();

        // Already submitted? then treat as done. Bypass when recovery action requires resubmit (broker/journal diverge).
        if (!_entryOrderRecoveryState.IsPending && _executionJournal != null &&
            (_executionJournal.IsIntentSubmitted(longIntentId, TradingDate, Stream) ||
             _executionJournal.IsIntentSubmitted(shortIntentId, TradingDate, Stream)))
        {
            _stopBracketsSubmittedAtLock = true;
            _journal.StopBracketsSubmittedAtLock = true; // PERSIST: Update journal
            _journals.Save(_journal);
            return;
        }

        // CRITICAL: Position check before resubmit - prevent double exposure.
        // Reconciliation may have run when flat, but a fill could have occurred before this tick.
        if (_entryOrderRecoveryState.IsPending && _executionAdapter != null)
        {
            try
            {
                var snap = _executionAdapter.GetAccountSnapshot(utcNow);
                var posQty = snap.Positions?.Where(p => IsSameInstrument(p.Instrument)).Sum(p => p.Quantity) ?? 0;
                if (posQty != 0)
                {
                    ClearRecoveryAction(utcNow);
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "ENTRY_ORDERS_RESUBMIT_BLOCKED_POSITION_NOT_FLAT", State.ToString(),
                        new
                        {
                            stream_id = Stream,
                            trading_date = TradingDate,
                            position_qty = posQty,
                            reason = "double_exposure_prevention",
                            note = "Resubmit blocked - position not flat; would create double exposure"
                        }));
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDERS_RESUBMIT_POSITION_CHECK_ERROR", State.ToString(),
                    new { error = ex.Message, note = "Position check failed - blocking resubmit for safety" }));
                return;
            }
        }

        try
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}", Instrument, "STOP_BRACKETS_SUBMIT_ATTEMPT", new
            {
                stream_id = Stream,
                trading_date = TradingDate,
                slot_time_chicago = SlotTimeChicago,
                brk_long = brkLong,
                brk_short = brkShort,
                oco_group = ocoGroup,
                long_stop_price = longStop,
                long_target_price = longTarget,
                long_be_trigger = longBeTrigger,
                short_stop_price = shortStop,
                short_target_price = shortTarget,
                short_be_trigger = shortBeTrigger,
                note = "Submitting paired stop-market entry orders at RANGE_LOCKED"
            }));

            // CRITICAL: Register intents BEFORE order submission so protective orders can be placed on fill
            // This MUST happen before SubmitStopEntryOrder() is called
            if (_executionAdapter is NinjaTraderSimAdapter ntAdapter)
            {
                ntAdapter.RegisterIntent(longIntent);
                ntAdapter.RegisterIntent(shortIntent);
                
                // CRITICAL FIX: Register policy expectations BEFORE order submission
                // This is required for pre-submission validation checks
                ntAdapter.RegisterIntentPolicy(longIntentId, _orderQuantity, _maxQuantity,
                    CanonicalInstrument, ExecutionInstrument, "EXECUTION_POLICY_FILE");
                ntAdapter.RegisterIntentPolicy(shortIntentId, _orderQuantity, _maxQuantity,
                    CanonicalInstrument, ExecutionInstrument, "EXECUTION_POLICY_FILE");
            }
            else
            {
                // CRITICAL ERROR: Execution adapter is not NinjaTraderSimAdapter - intents cannot be registered
                // This will cause protective orders to fail on fill
                _log.Write(RobotEvents.ExecutionBase(utcNow, $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}", Instrument, "EXECUTION_ERROR",
                    new
                    {
                        error = "Execution adapter is not NinjaTraderSimAdapter - RegisterIntent() cannot be called",
                        execution_adapter_type = _executionAdapter?.GetType().Name ?? "NULL",
                        long_intent_id = longIntentId,
                        short_intent_id = shortIntentId,
                        note = "CRITICAL: Protective orders will NOT be placed on fill because intents are not registered"
                    }));
            }

            // Pre-submit: a crossed breakout stop is either converted to MARKET within tolerance or rejected as missed.
            var (bid, ask) = _executionAdapter.GetCurrentMarketPrice(ExecutionInstrument, utcNow);
            var toleranceTicks = GetBreakoutValidityToleranceTicks();
            var longSubmit = ResolveBreakoutEntrySubmitAction("Long", brkLong, bid, ask, toleranceTicks);
            var shortSubmit = ResolveBreakoutEntrySubmitAction("Short", brkShort, bid, ask, toleranceTicks);
            var longDecision = longSubmit.Action.ToString().ToUpperInvariant();
            var shortDecision = shortSubmit.Action.ToString().ToUpperInvariant();

            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "ENTRY_ORDER_TYPE_DECISION", State.ToString(),
                new { stream = Stream, side = "LONG", decision = longDecision, bid, ask, brk_long = brkLong, brk_short = brkShort, crossed_distance_ticks = longSubmit.DistanceTicks, tolerance_ticks = toleranceTicks, reason = longSubmit.Reason }));
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "ENTRY_ORDER_TYPE_DECISION", State.ToString(),
                new { stream = Stream, side = "SHORT", decision = shortDecision, bid, ask, brk_long = brkLong, brk_short = brkShort, crossed_distance_ticks = shortSubmit.DistanceTicks, tolerance_ticks = toleranceTicks, reason = shortSubmit.Reason }));

            if (longSubmit.Action == BreakoutEntrySubmitAction.Reject ||
                shortSubmit.Action == BreakoutEntrySubmitAction.Reject)
            {
                MarkPostLockBreakoutFromRejectedSide(
                    longSubmit.Action == BreakoutEntrySubmitAction.Reject,
                    shortSubmit.Action == BreakoutEntrySubmitAction.Reject);
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_BREAKOUT_TOO_FAR_REJECTED", State.ToString(),
                    new
                    {
                        stream = Stream,
                        bid,
                        ask,
                        brk_long = brkLong,
                        brk_short = brkShort,
                        long_decision = longDecision,
                        short_decision = shortDecision,
                        long_crossed_distance_ticks = longSubmit.DistanceTicks,
                        short_crossed_distance_ticks = shortSubmit.DistanceTicks,
                        tolerance_ticks = toleranceTicks,
                        note = "Breakout price was crossed beyond tolerance before bracket submission; treating as missed opportunity."
                    }));
                LogSlotEndSummary(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", false, false,
                    "Range locked; breakout crossed beyond tolerance before entry could be submitted");
                _ = Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED");
                return;
            }

            OrderSubmissionResult longRes;
            OrderSubmissionResult shortRes;
            if (longSubmit.Action == BreakoutEntrySubmitAction.Market)
                longRes = _executionAdapter.SubmitEntryOrder(longIntentId, ExecutionInstrument, "Long", null, _orderQuantity, "MARKET", ocoGroup, utcNow);
            else
                longRes = _executionAdapter.SubmitStopEntryOrder(longIntentId, ExecutionInstrument, "Long", brkLong, _orderQuantity, ocoGroup, utcNow);
            if (shortSubmit.Action == BreakoutEntrySubmitAction.Market)
                shortRes = _executionAdapter.SubmitEntryOrder(shortIntentId, ExecutionInstrument, "Short", null, _orderQuantity, "MARKET", ocoGroup, utcNow);
            else
                shortRes = _executionAdapter.SubmitStopEntryOrder(shortIntentId, ExecutionInstrument, "Short", brkShort, _orderQuantity, ocoGroup, utcNow);

            // Persist to execution journal for idempotency (record both attempts)
            // PHASE 2: Journal uses ExecutionInstrument for execution tracking
            if (longRes.Success)
                _executionJournal.RecordSubmission(
                    longIntentId,
                    TradingDate,
                    Stream,
                    ExecutionInstrument,
                    "ENTRY_STOP_LONG",
                    longRes.BrokerOrderId,
                    utcNow,
                    expectedEntryPrice: brkLong,
                    entryPrice: brkLong,
                    stopPrice: longStop,
                    targetPrice: longTarget,
                    beTriggerPrice: longBeTrigger,
                    direction: "Long",
                    ocoGroup: ocoGroup);
            else
            {
                var longErr = longRes.ErrorMessage ?? "ENTRY_STOP_LONG_FAILED";
                _executionJournal.RecordRejection(longIntentId, TradingDate, Stream, longErr, utcNow);
                if (longErr.IndexOf("price outside limits", StringComparison.OrdinalIgnoreCase) >= 0)
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "ENTRY_ORDER_REJECTED_PRICE_INVALID", State.ToString(),
                        new { stream = Stream, side = "LONG", submitted_price = brkLong, current_bid = bid, current_ask = ask }));
            }

            if (shortRes.Success)
                _executionJournal.RecordSubmission(
                    shortIntentId,
                    TradingDate,
                    Stream,
                    ExecutionInstrument,
                    "ENTRY_STOP_SHORT",
                    shortRes.BrokerOrderId,
                    utcNow,
                    expectedEntryPrice: brkShort,
                    entryPrice: brkShort,
                    stopPrice: shortStop,
                    targetPrice: shortTarget,
                    beTriggerPrice: shortBeTrigger,
                    direction: "Short",
                    ocoGroup: ocoGroup);
            else
            {
                var shortErr = shortRes.ErrorMessage ?? "ENTRY_STOP_SHORT_FAILED";
                _executionJournal.RecordRejection(shortIntentId, TradingDate, Stream, shortErr, utcNow);
                if (shortErr.IndexOf("price outside limits", StringComparison.OrdinalIgnoreCase) >= 0)
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "ENTRY_ORDER_REJECTED_PRICE_INVALID", State.ToString(),
                        new { stream = Stream, side = "SHORT", submitted_price = brkShort, current_bid = bid, current_ask = ask }));
            }

            if (longRes.Success && shortRes.Success)
            {
                _stopBracketsSubmittedAtLock = true;
                
                // PERSIST: Save flag to journal immediately
                _journal.StopBracketsSubmittedAtLock = true;
                _journals.Save(_journal);
                
                if (_entryOrderRecoveryState.IsPending)
                {
                    ClearRecoveryAction(utcNow);
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "ENTRY_ORDERS_RESUBMITTED", State.ToString(),
                        new
                        {
                            stream_id = Stream,
                            trading_date = TradingDate,
                            long_intent_id = longIntentId,
                            short_intent_id = shortIntentId,
                            long_broker_order_id = longRes.BrokerOrderId,
                            short_broker_order_id = shortRes.BrokerOrderId,
                            reason = "reconciliation_resubmit",
                            note = "Entry orders resubmitted after reconciliation detected missing/invalid orders"
                        }));
                }
                
                _log.Write(RobotEvents.ExecutionBase(utcNow, $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}", Instrument, "STOP_BRACKETS_SUBMITTED", new
                {
                    stream_id = Stream,
                    trading_date = TradingDate,
                    slot_time_chicago = SlotTimeChicago,
                    long_intent_id = longIntentId,
                    short_intent_id = shortIntentId,
                    long_broker_order_id = longRes.BrokerOrderId,
                    short_broker_order_id = shortRes.BrokerOrderId,
                    oco_group = ocoGroup,
                    persisted_to_journal = true,
                    note = "Stop entry brackets submitted; breakout fill should not require additional submission"
                }));
                
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BRACKET_SUBMIT_OUTCOME", State.ToString(),
                    new
                    {
                        outcome = "submitted_both",
                        stream_id = Stream,
                        trading_date = TradingDate,
                        slot_time_chicago = SlotTimeChicago,
                        note = "Post-submit truth: both stop entry brackets accepted by adapter"
                    }));
                LogSlotEndSummary(utcNow, "RANGE_VALID", true, false, "Range locked; stop brackets submitted, awaiting fill");
            }
            else
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}", Instrument, "STOP_BRACKETS_SUBMIT_FAILED", new
                {
                    stream_id = Stream,
                    trading_date = TradingDate,
                    slot_time_chicago = SlotTimeChicago,
                    oco_group = ocoGroup,
                    long_success = longRes.Success,
                    long_error = longRes.ErrorMessage,
                    short_success = shortRes.Success,
                    short_error = shortRes.ErrorMessage,
                    note = "Failed to submit one or both stop entry brackets"
                }));
                var bothRejected = !longRes.Success && !shortRes.Success;
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BRACKET_SUBMIT_OUTCOME", State.ToString(),
                    new
                    {
                        outcome = bothRejected ? "rejected_both" : "rejected_partial",
                        stream_id = Stream,
                        trading_date = TradingDate,
                        slot_time_chicago = SlotTimeChicago,
                        long_success = longRes.Success,
                        short_success = shortRes.Success,
                        note = "Post-submit truth: stop entry bracket submission did not succeed for both sides"
                    }));
                LogSlotEndSummary(utcNow, "RANGE_VALID", true, false,
                    bothRejected
                        ? "Range locked; stop bracket submission failed (both sides) — no working entry orders"
                        : "Range locked; stop bracket submission incomplete (one side) — review adapter errors");
                var partialRejected = longRes.Success != shortRes.Success;
                if (partialRejected && _executionAdapter is NinjaTraderSimAdapter partialCancelAdapter)
                {
                    try
                    {
                        if (longRes.Success) partialCancelAdapter.CancelIntentOrders(longIntentId, utcNow);
                        if (shortRes.Success) partialCancelAdapter.CancelIntentOrders(shortIntentId, utcNow);
                        _log.Write(RobotEvents.ExecutionBase(utcNow, $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}", Instrument, "STOP_BRACKETS_PARTIAL_CANCELLED", new
                        {
                            stream_id = Stream,
                            trading_date = TradingDate,
                            long_intent_id = longIntentId,
                            short_intent_id = shortIntentId,
                            long_success = longRes.Success,
                            short_success = shortRes.Success,
                            note = "Paired entry brackets are all-or-none; accepted side was cancelled after sibling rejection."
                        }));
                    }
                    catch (Exception cancelEx)
                    {
                        _log.Write(RobotEvents.ExecutionBase(utcNow, $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}", Instrument, "STOP_BRACKETS_PARTIAL_CANCEL_FAILED", new
                        {
                            stream_id = Stream,
                            trading_date = TradingDate,
                            error = cancelEx.Message,
                            note = "Failed to cancel accepted side after partial entry-bracket rejection."
                        }));
                    }
                }

                var marketMovedLong = !longRes.Success && IsEntryStopMarketMovedRejection(longRes.ErrorMessage);
                var marketMovedShort = !shortRes.Success && IsEntryStopMarketMovedRejection(shortRes.ErrorMessage);
                if (marketMovedLong || marketMovedShort)
                {
                    MarkPostLockBreakoutFromRejectedSide(marketMovedLong, marketMovedShort);
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "ENTRY_INVALIDATED_POST_LOCK_EXCURSION", State.ToString(),
                        new
                        {
                            stream_id = Stream,
                            trading_date = TradingDate,
                            breakout_long = brkLong,
                            breakout_short = brkShort,
                            long_touched = _journal.PostLockLongBreakoutTouched,
                            short_touched = _journal.PostLockShortBreakoutTouched,
                            long_error = longRes.ErrorMessage,
                            short_error = shortRes.ErrorMessage,
                            source = "BROKER_REJECTED_MARKET_MOVED",
                            note = "Broker rejected one stop-entry side as already marketable; strict setup expired."
                        }));
                    _ = Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED");
                }
                else if (bothRejected || partialRejected)
                {
                    // Terminal NO_TRADE: avoid substring FAILED in commit reason (classified as FAILED_RUNTIME elsewhere)
                    _ = Commit(utcNow, "NO_TRADE_ENTRY_BRACKETS_AT_LOCK_REJECTED", "NO_TRADE_ENTRY_BRACKETS_AT_LOCK_REJECTED");
                }
            }
        }
        catch (Exception ex)
        {
            // CRITICAL: Catch all exceptions to prevent crashes
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_SUBMIT_EXCEPTION", State.ToString(),
                new 
                { 
                    exception_type = ex.GetType().Name,
                    exception_message = ex.Message,
                    stack_trace = ex.StackTrace,
                    stream_id = Stream,
                    trading_date = TradingDate,
                    brk_long = brkLong,
                    brk_short = brkShort,
                    long_intent_id = longIntentId,
                    short_intent_id = shortIntentId,
                    note = "Exception during stop brackets submission - orders not placed"
                }));
        }
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

    /// <summary>
    /// Perform one-time pre-hydration at strategy enable.
    /// Reads external raw data file and inserts bars into the same buffer used by OnBarUpdate.
    /// Works for both SIM and DRYRUN modes.
    /// </summary>
}
