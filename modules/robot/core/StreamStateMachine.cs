using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Globalization;

namespace QTSW2.Robot.Core;

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

public sealed class StreamStateMachine
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
    
    // Chicago time boundaries for range computation (authoritative for bar filtering)
    // CRITICAL: These are DateTimeOffset values in Chicago timezone, not strings
    private DateTimeOffset RangeStartChicagoTime { get; set; }
    private DateTimeOffset SlotTimeChicagoTime { get; set; }

    private readonly TimeService _time;
    private readonly ParitySpec _spec;
    private readonly RobotLogger _log;
    private readonly JournalStore _journals;
    private readonly string _projectRoot;
    private readonly RangeLockedEventPersister? _rangePersister;
    private readonly HydrationEventPersister? _hydrationPersister;
    private StreamJournal _journal;
    private readonly decimal _tickSize;
    private readonly string _timetableHash;
    private readonly ExecutionMode _executionMode;
    private readonly decimal _baseTarget;
    private readonly IExecutionAdapter? _executionAdapter;
    private readonly RiskGate? _riskGate;
    private readonly ExecutionJournal? _executionJournal;
    private readonly RobotEngine? _engine; // Optional: engine reference for BarsRequest status checks
    private readonly int _orderQuantity; // PHASE 3.2: Fixed order quantity for all intents (code-controlled)
    private readonly int _maxQuantity; // Policy max_size
    private bool _stopBracketsSubmittedAtLock = false;
    
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
    private DateTimeOffset? _lastTickTraceUtc = null; // Rate-limiting for tick trace logging
    private DateTimeOffset? _lastTickCalledUtc = null; // Rate-limiting for tick call logging
    
    // Logging rate limiting
    private bool _rangeComputeStartLogged = false; // Ensure RANGE_COMPUTE_START only logged once per slot
    private DateTimeOffset? _lastRangeComputeFailedLogUtc = null; // Rate-limit RANGE_COMPUTE_FAILED (once per minute max)
    private DateTimeOffset? _lastBarFilteringLogUtc = null; // Rate-limit RANGE_COMPUTE_BAR_FILTERING (once per minute max)
    private DateTimeOffset? _stateEntryTimeUtc = null; // Track when current state was entered (for stuck detection)
    private DateTimeOffset? _lastStuckStateCheckUtc = null; // Rate-limit stuck state checks (once per 5 minutes max)
    private DateTimeOffset? _lastLiveBarAgeWarningUtc = null; // Rate-limit BAR_PARTIAL_WARNING_LIVE_FEED (once per stream per 5 minutes)
    private DateTimeOffset? _lastBarBufferedStateIndependentUtc = null; // Rate-limit BAR_BUFFERED_STATE_INDEPENDENT (once per stream per 5 minutes)
    private DateTimeOffset? _lastPreHydrationHandlerTraceUtc = null; // Rate-limit PRE_HYDRATION_HANDLER_TRACE (once per stream per 5 minutes)
    private DateTimeOffset? _lastArmedWaitingForBarsLogUtc = null; // Rate-limit ARMED_WAITING_FOR_BARS (once per stream per 5 minutes)
    
    // Range tracking for change detection
    private decimal? _lastLoggedRangeHigh = null; // Track last logged range high for change detection
    private decimal? _lastLoggedRangeLow = null; // Track last logged range low for change detection
    
    // Assertion flags (once per stream per day)
    private bool _rangeIntentAssertEmitted = false; // RANGE_INTENT_ASSERT emitted
    private bool _firstBarAcceptedAssertEmitted = false; // RANGE_FIRST_BAR_ACCEPTED emitted
    private bool _rangeLockAssertEmitted = false; // RANGE_LOCK_ASSERT emitted

    private decimal? _lastCloseBeforeLock;

    // Dry-run entry tracking
    private bool _entryDetected;
    private string? _intendedDirection; // "Long", "Short", or null
    private decimal? _intendedEntryPrice;
    private DateTimeOffset? _intendedEntryTimeUtc;
    private string? _triggerReason; // "IMMEDIATE_AT_LOCK", "BREAKOUT", "NO_TRADE_MARKET_CLOSE"
    private decimal? _brkLongRaw;
    private decimal? _brkShortRaw;
    private decimal? _brkLongRounded;
    private decimal? _brkShortRounded;
    
    // Execution tracking
    private decimal? _intendedStopPrice;
    private decimal? _intendedTargetPrice;
    private decimal? _intendedBeTrigger;

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
        string projectRoot, // Project root directory for range event persistence
        IExecutionAdapter? executionAdapter = null,
        RiskGate? riskGate = null,
        ExecutionJournal? executionJournal = null,
        LoggingConfig? loggingConfig = null, // Optional: logging configuration for diagnostic control
        RobotEngine? engine = null // Optional: engine reference for BarsRequest status checks
    )
    {
        _time = time;
        _spec = spec;
        _log = log;
        _journals = journals;
        _projectRoot = projectRoot;
        _timetableHash = timetableHash;
        _executionMode = executionMode;
        _executionAdapter = executionAdapter;
        _riskGate = riskGate;
        _executionJournal = executionJournal;
        _engine = engine;
        
        // Initialize range event persister (singleton)
        _rangePersister = RangeLockedEventPersister.GetInstance(_projectRoot);
        
        // Initialize hydration event persister (singleton)
        _hydrationPersister = HydrationEventPersister.GetInstance(_projectRoot);
        
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

        var existing = journals.TryLoad(tradingDateStr, Stream);
        var isRestart = existing != null;
        var isMidSessionRestart = false;
        
        if (isRestart)
        {
            // Check if this is a mid-session restart (stream was already initialized today)
            var nowUtc = DateTimeOffset.UtcNow;
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
            _journal.ReentrySubmitted = existing.ReentrySubmitted;
            _journal.ReentryFilled = existing.ReentryFilled;
            _journal.ProtectionSubmitted = existing.ProtectionSubmitted;
            _journal.ProtectionAccepted = existing.ProtectionAccepted;
            _journal.NextSlotTimeUtc = existing.NextSlotTimeUtc ?? CalculateNextSlotTimeUtc(tradingDate, SlotTimeChicago, time);
            _journal.PriorJournalKey = existing.PriorJournalKey;
        }
        
        // Initialize state entry time tracking
        _stateEntryTimeUtc = DateTimeOffset.UtcNow;
        
        // RESTART RECOVERY: Restore flags from persisted state
        if (isRestart && existing != null)
        {
            // Restore order submission flag from journal
            _stopBracketsSubmittedAtLock = existing.StopBracketsSubmittedAtLock;
            
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
                // First, try to restore from hydration/ranges log (canonical source)
                RestoreRangeLockedFromHydrationLog(tradingDateStr, Stream);
                
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
            }
        }
        
        // Log STREAM_INITIALIZED hydration event
        try
        {
            var utcNow = DateTimeOffset.UtcNow;
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

    public void ApplyDirectiveUpdate(string newSlotTimeChicago, DateOnly tradingDate, DateTimeOffset utcNow)
    {
        // NQ2 FIX: Prevent slot_time changes after stream initialization if stream is past PRE_HYDRATION
        // This prevents timetable updates from changing slot_time mid-session
        if (State != StreamState.PRE_HYDRATION && SlotTimeChicago != newSlotTimeChicago)
        {
            var warningMsg = $"WARNING: Attempted to update slot_time from '{SlotTimeChicago}' to '{newSlotTimeChicago}' " +
                           $"but stream is already in state '{State}'. Slot_time changes are only allowed during PRE_HYDRATION. " +
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
            
            return; // Reject the update
        }
        
        // Allowed only if not committed
        SlotTimeChicago = newSlotTimeChicago;
        
        // PHASE 1: Construct Chicago time directly (authoritative)
        SlotTimeChicagoTime = _time.ConstructChicagoTime(tradingDate, newSlotTimeChicago);
        
        // PHASE 2: Derive UTC from Chicago time (derived representation)
        SlotTimeUtc = _time.ConvertChicagoToUtc(SlotTimeChicagoTime);
        
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, "UPDATE_APPLIED", State.ToString(),
            new { slot_time_chicago = SlotTimeChicago, slot_time_chicago_time = SlotTimeChicagoTime.ToString("o"), slot_time_utc = SlotTimeUtc.ToString("o") }));
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
        var existingJournal = _journals.TryLoad(newTradingDateStr, Stream);
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
        var previousJournal = _journals.TryLoad(previousTradingDateStr, Stream);
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
        
        // SLOT PERSISTENCE: Check for market open re-entry (time-based, not bar-based)
        if (_journal.SlotStatus == SlotStatus.ACTIVE && _journal.ExecutionInterruptedByClose && !_journal.ReentrySubmitted)
        {
            CheckMarketOpenReentry(utcNow);
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
                        
                        Commit(utcNow, "NO_TRADE_LATE_START_MISSED_BREAKOUT", "NO_TRADE_LATE_START_MISSED_BREAKOUT");
                        return; // Do not transition to ARMED
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
                        LogNoTradeMarketClose(utcNow);
                        Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE");
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
                    _rangeComputeStartLogged = false;
                    _lastRangeComputeFailedLogUtc = null;
                    
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
        if (!_entryDetected && utcNow >= MarketCloseUtc)
        {
            LogNoTradeMarketClose(utcNow);
            Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE");
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
            
            // Legacy check: If range is locked but state is not RANGE_LOCKED, log critical error
            if (_rangeLocked && State != StreamState.RANGE_LOCKED)
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
            var longIntentId = ComputeIntentId("Long", _brkLongRounded.Value, SlotTimeUtc, "STOP_BRACKETS_AT_LOCK");
            var shortIntentId = ComputeIntentId("Short", _brkShortRounded.Value, SlotTimeUtc, "STOP_BRACKETS_AT_LOCK");
            
            bool alreadySubmitted = false;
            if (_executionJournal != null)
            {
                alreadySubmitted = _executionJournal.IsIntentSubmitted(longIntentId, TradingDate, Stream) ||
                                  _executionJournal.IsIntentSubmitted(shortIntentId, TradingDate, Stream);
            }
            
            if (!alreadySubmitted)
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
        
        // Check for market close cutoff (all execution modes)
        if (!_entryDetected && utcNow >= MarketCloseUtc)
        {
            LogNoTradeMarketClose(utcNow);
            Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE");
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
        _lastTickTraceUtc = null;
        _lastTickCalledUtc = null;
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
        Commit(utcNow, "STREAM_STAND_DOWN", "STREAM_STAND_DOWN");
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
        
        // DIAGNOSTIC: Log bar reception details (only if diagnostic logs enabled)
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
        
        // Buffer bars that fall within [range_start, slot_time] using Chicago time comparison
        // Bar timestamps represent OPEN time (converted from NinjaTrader close time for Analyzer parity)
        // Range window is defined in Chicago time to match trading session semantics
        // State-independent buffering: Always buffer bars within range window regardless of state
        // CRITICAL FIX: Include slot_time bar (<= instead of <) so range lock check runs when slot_time bar arrives
        
        // DIAGNOSTIC: Proof log for 1-minute boundary investigation
        // Log every bar admission decision with raw timestamp, Chicago time, comparison result, and source
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
                note = "Diagnostic proof log - bar timestamps represent OPEN time (converted from NinjaTrader close time for Analyzer parity)"
            }));
        
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

            // Check for breakout after lock (before market close) - FIXED: Now works for all execution modes
            if (!_entryDetected && barUtc >= SlotTimeUtc && barUtc < MarketCloseUtc && _brkLongRounded.HasValue && _brkShortRounded.HasValue)
            {
                CheckBreakoutEntry(barUtc, high, low, utcNow);
            }
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
    /// Submit paired stop-market entry orders (long + short) immediately after RANGE_LOCKED.
    /// These are linked via OCO so only one side can fill.
    /// </summary>
    private void SubmitStopEntryBracketsAtLock(DateTimeOffset utcNow)
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

        // Idempotency: only once per stream per day
        if (_stopBracketsSubmittedAtLock)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_EARLY_RETURN", State.ToString(),
                new { reason = "IDEMPOTENCY", _stopBracketsSubmittedAtLock = true }));
            return;
        }

        // Preconditions
        if (_journal.Committed || State == StreamState.DONE)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_EARLY_RETURN", State.ToString(),
                new { reason = "JOURNAL_COMMITTED_OR_DONE", journal_committed = _journal?.Committed ?? false, state = State.ToString() }));
            return;
        }
        if (_rangeInvalidated)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_EARLY_RETURN", State.ToString(),
                new { reason = "RANGE_INVALIDATED", _rangeInvalidated = true }));
            return;
        }
        // Trading gate: Block entries if breakout levels are missing
        if (_breakoutLevelsMissing)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_EARLY_RETURN", State.ToString(),
                new { reason = "BREAKOUT_LEVELS_MISSING", _breakoutLevelsMissing = true, note = "Stream gated from entry until breakout levels are computed" }));
            return;
        }
        if (_executionAdapter == null || _executionJournal == null || _riskGate == null)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_EARLY_RETURN", State.ToString(),
                new 
                { 
                    reason = "NULL_DEPENDENCIES",
                    execution_adapter_null = _executionAdapter == null,
                    execution_journal_null = _executionJournal == null,
                    risk_gate_null = _riskGate == null
                }));
            return;
        }
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_EARLY_RETURN", State.ToString(),
                new 
                { 
                    reason = "BREAKOUT_LEVELS_MISSING",
                    brk_long_has_value = _brkLongRounded.HasValue,
                    brk_short_has_value = _brkShortRounded.HasValue
                }));
            return;
        }
        if (!RangeHigh.HasValue || !RangeLow.HasValue)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_EARLY_RETURN", State.ToString(),
                new 
                { 
                    reason = "RANGE_VALUES_MISSING",
                    range_high_has_value = RangeHigh.HasValue,
                    range_low_has_value = RangeLow.HasValue
                }));
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
            Session,
            SlotTimeChicago,
            "Long",
            brkLong,
            stopPrice: longStop,
            targetPrice: longTarget,
            beTrigger: longBeTrigger,
            entryTimeUtc: utcNow,
            triggerReason: "STOP_BRACKETS_AT_LOCK");

        var shortIntent = new Intent(
            TradingDate,
            Stream,
            Instrument,
            Session,
            SlotTimeChicago,
            "Short",
            brkShort,
            stopPrice: shortStop,
            targetPrice: shortTarget,
            beTrigger: shortBeTrigger,
            entryTimeUtc: utcNow,
            triggerReason: "STOP_BRACKETS_AT_LOCK");

        var longIntentId = longIntent.ComputeIntentId();
        var shortIntentId = shortIntent.ComputeIntentId();

        // Already submitted? then treat as done.
        if (_executionJournal != null && 
            (_executionJournal.IsIntentSubmitted(longIntentId, TradingDate, Stream) ||
             _executionJournal.IsIntentSubmitted(shortIntentId, TradingDate, Stream)))
        {
            _stopBracketsSubmittedAtLock = true;
            _journal.StopBracketsSubmittedAtLock = true; // PERSIST: Update journal
            _journals.Save(_journal);
            return;
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

            // PHASE 2: Use ExecutionInstrument for order placement (not canonical Instrument)
            // PHASE 3: Use ExecutionInstrument for order placement (explicit, not ambiguous Instrument property)
            // PHASE 3.2: Use code-controlled order quantity (Chart Trader quantity ignored)
            var longRes = _executionAdapter.SubmitStopEntryOrder(longIntentId, ExecutionInstrument, "Long", brkLong, _orderQuantity, ocoGroup, utcNow);
            var shortRes = _executionAdapter.SubmitStopEntryOrder(shortIntentId, ExecutionInstrument, "Short", brkShort, _orderQuantity, ocoGroup, utcNow);

            // Persist to execution journal for idempotency (record both attempts)
            // PHASE 2: Journal uses ExecutionInstrument for execution tracking
            if (longRes.Success)
                _executionJournal.RecordSubmission(longIntentId, TradingDate, Stream, ExecutionInstrument, "ENTRY_STOP_LONG", longRes.BrokerOrderId, utcNow);
            else
                _executionJournal.RecordRejection(longIntentId, TradingDate, Stream, longRes.ErrorMessage ?? "ENTRY_STOP_LONG_FAILED", utcNow);

            if (shortRes.Success)
                _executionJournal.RecordSubmission(shortIntentId, TradingDate, Stream, ExecutionInstrument, "ENTRY_STOP_SHORT", shortRes.BrokerOrderId, utcNow);
            else
                _executionJournal.RecordRejection(shortIntentId, TradingDate, Stream, shortRes.ErrorMessage ?? "ENTRY_STOP_SHORT_FAILED", utcNow);

            if (longRes.Success && shortRes.Success)
            {
                _stopBracketsSubmittedAtLock = true;
                
                // PERSIST: Save flag to journal immediately
                _journal.StopBracketsSubmittedAtLock = true;
                _journals.Save(_journal);
                
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
    private void PerformPreHydration(DateTimeOffset utcNow)
    {
        
        try
        {
            var nowChicago = _time.ConvertUtcToChicago(utcNow);
            
            // Compute hydration window: [range_start, min(now, range_end))
            var hydrationStart = RangeStartChicagoTime;
            var hydrationEnd = nowChicago < SlotTimeChicagoTime ? nowChicago : SlotTimeChicagoTime;
            
            // Resolve project root using deterministic method (not CWD)
            string projectRoot;
            try
            {
                projectRoot = ProjectRootResolver.ResolveProjectRoot();
            }
            catch (Exception ex)
            {
                LogHealth("ERROR", "PRE_HYDRATION_FAILED", $"Failed to resolve project root: {ex.Message}",
                    new
                    {
                        instrument = Instrument,
                        slot = Stream,
                        trading_date = TradingDate
                    });
                _preHydrationComplete = true; // Mark complete to allow progression (will fail later if needed)
                return;
            }
            
            // Construct file path: data/raw/{instrument}/1m/{yyyy}/{MM}/{yyyy-MM-dd}.csv
            var tradingDateParts = TradingDate.Split('-');
            if (tradingDateParts.Length != 3)
            {
                LogHealth("ERROR", "PRE_HYDRATION_FAILED", "Invalid trading date format",
                    new { instrument = Instrument, slot = Stream, trading_date = TradingDate });
                _preHydrationComplete = true;
                return;
            }
            
            if (!int.TryParse(tradingDateParts[0], out var year) ||
                !int.TryParse(tradingDateParts[1], out var month) ||
                !int.TryParse(tradingDateParts[2], out var day))
            {
                LogHealth("ERROR", "PRE_HYDRATION_FAILED", "Failed to parse trading date",
                    new { instrument = Instrument, slot = Stream, trading_date = TradingDate });
                _preHydrationComplete = true;
                return;
            }
            
            var fileDir = Path.Combine(projectRoot, "data", "raw", Instrument.ToLowerInvariant(), "1m", year.ToString("0000"), month.ToString("00"));
            // File naming pattern: {INSTRUMENT}_1m_{yyyy-MM-dd}.csv (e.g., ES_1m_2026-01-13.csv)
            var fileName = $"{Instrument.ToUpperInvariant()}_1m_{year:0000}-{month:00}-{day:00}.csv";
            var filePath = Path.Combine(fileDir, fileName);
            
            // Log fully resolved absolute path before reading
            var absolutePath = Path.GetFullPath(filePath);
            LogHealth("INFO", "PRE_HYDRATION_START", "Starting pre-hydration",
                new
                {
                    instrument = Instrument,
                    slot = Stream,
                    trading_date = TradingDate,
                    resolved_file_path = absolutePath,
                    hydration_start_chicago = hydrationStart.ToString("o"),
                    hydration_end_chicago = hydrationEnd.ToString("o")
                });
            
            if (!File.Exists(filePath))
            {
                var logLevel = nowChicago >= RangeStartChicagoTime ? "ERROR" : "WARN";
                LogHealth(logLevel, "PRE_HYDRATION_ZERO_BARS", "Pre-hydration file not found - zero bars loaded",
                    new
                    {
                        instrument = Instrument,
                        slot = Stream,
                        resolved_file_path = absolutePath,
                        hydration_start_chicago = hydrationStart.ToString("o"),
                        hydration_end_chicago = hydrationEnd.ToString("o"),
                        now_chicago = nowChicago.ToString("o"),
                        range_start_chicago = RangeStartChicagoTime.ToString("o")
                    });
                _hadZeroBarHydration = true; // Mark zero-bar hydration
                _preHydrationComplete = true;
                return;
            }
            
            // Read and parse CSV line-by-line
            var hydratedBars = new List<Bar>();
            var chicagoTz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
            
            using (var reader = new StreamReader(filePath))
            {
                // Skip header line
                var header = reader.ReadLine();
                if (header == null)
                {
                    var logLevel = nowChicago >= RangeStartChicagoTime ? "ERROR" : "WARN";
                    LogHealth(logLevel, "PRE_HYDRATION_ZERO_BARS", "Pre-hydration file empty (no header) - zero bars loaded",
                        new
                        {
                            instrument = Instrument,
                            slot = Stream,
                            resolved_file_path = absolutePath,
                            hydration_start_chicago = hydrationStart.ToString("o"),
                            hydration_end_chicago = hydrationEnd.ToString("o")
                        });
                    _hadZeroBarHydration = true; // Mark zero-bar hydration
                    _preHydrationComplete = true;
                    return;
                }
                
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    
                    var parts = line.Split(',');
                    if (parts.Length < 5) continue;
                    
                    // Parse timestamp_utc (bar open time in UTC)
                    if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestampUtc))
                        continue;
                    
                    // Convert to Chicago time (bar OPEN time)
                    var barOpenChicago = TimeZoneInfo.ConvertTimeFromUtc(timestampUtc.DateTime, chicagoTz);
                    var barOpenChicagoOffset = new DateTimeOffset(barOpenChicago, chicagoTz.GetUtcOffset(barOpenChicago));
                    
                    // Filter to hydration window [hydration_start, hydration_end)
                    if (barOpenChicagoOffset < hydrationStart || barOpenChicagoOffset >= hydrationEnd)
                        continue;
                    
                    // Parse OHLCV
                    if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open) ||
                        !decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var high) ||
                        !decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var low) ||
                        !decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close))
                        continue;
                    
                    decimal? volume = null;
                    if (parts.Length > 5 && decimal.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var vol))
                        volume = vol;
                    
                    var bar = new Bar(timestampUtc, open, high, low, close, volume);
                    hydratedBars.Add(bar);
                }
            }
            
            // Sort by UTC timestamp
            hydratedBars.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
            
            // Insert hydrated bars into the same buffer used by OnBarUpdate (no source tagging)
            // Insert hydrated bars into the same buffer used by OnBarUpdate
            // Track as CSV bars (from file-based pre-hydration)
            // CRITICAL: All deduplication logic is centralized in AddBarToBuffer()
            // This ensures consistent precedence enforcement (LIVE > BARSREQUEST > CSV)
            foreach (var bar in hydratedBars)
            {
                AddBarToBuffer(bar, BarSource.CSV);
            }
            
            // Sort buffer to maintain chronological order (after all additions)
            lock (_barBufferLock)
            {
                _barBuffer.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
            }
            
            // Reset gap counters after pre-hydration completes
            _largestSingleGapMinutes = 0.0;
            _totalGapMinutes = 0.0;
            _lastBarOpenChicago = null;
            
            // Log PRE_HYDRATION summary
            var barSpanStart = hydratedBars.Count > 0 ? _time.ConvertUtcToChicago(hydratedBars[0].TimestampUtc) : (DateTimeOffset?)null;
            var barSpanEnd = hydratedBars.Count > 0 ? _time.ConvertUtcToChicago(hydratedBars[hydratedBars.Count - 1].TimestampUtc) : (DateTimeOffset?)null;
            
            if (hydratedBars.Count == 0)
            {
                var logLevel = nowChicago >= RangeStartChicagoTime ? "ERROR" : "WARN";
                LogHealth(logLevel, "PRE_HYDRATION_ZERO_BARS", "Pre-hydration loaded zero bars",
                    new
                    {
                        instrument = Instrument,
                        slot = Stream,
                        resolved_file_path = absolutePath,
                        hydration_start_chicago = hydrationStart.ToString("o"),
                        hydration_end_chicago = hydrationEnd.ToString("o"),
                        now_chicago = nowChicago.ToString("o"),
                        range_start_chicago = RangeStartChicagoTime.ToString("o")
                    });
                _hadZeroBarHydration = true; // Mark zero-bar hydration
            }
            else
            {
                LogHealth("INFO", "PRE_HYDRATION_COMPLETE", $"Pre-hydration complete - {hydratedBars.Count} bars loaded",
                    new
                    {
                        instrument = Instrument,
                        slot = Stream,
                        trading_date = TradingDate,
                        resolved_file_path = absolutePath,
                        bars_loaded = hydratedBars.Count,
                        bar_span_start_chicago = barSpanStart?.ToString("o"),
                        bar_span_end_chicago = barSpanEnd?.ToString("o"),
                        hydration_start_chicago = hydrationStart.ToString("o"),
                        hydration_end_chicago = hydrationEnd.ToString("o"),
                        timestamp_chicago = nowChicago.ToString("o")
                    });
            }
            
            _preHydrationComplete = true;
        }
        catch (Exception ex)
        {
            LogHealth("ERROR", "PRE_HYDRATION_ERROR", $"Pre-hydration failed with exception: {ex.Message}",
                new
                {
                    instrument = Instrument,
                    slot = Stream,
                    trading_date = TradingDate,
                    error = ex.ToString()
                });
            _preHydrationComplete = true; // Mark complete to allow progression
        }
    }
    
    /// <summary>
    /// Check if stream has sufficient bars for range calculation.
    /// </summary>
    /// <param name="actualCount">Actual bar count in buffer</param>
    /// <param name="expected">Output: Expected bar count based on time window</param>
    /// <param name="required">Output: Minimum required bar count (based on threshold)</param>
    /// <param name="thresholdPercent">Threshold percentage (default 0.85 = 85%)</param>
    /// <returns>True if actualCount >= required, false otherwise</returns>
    private bool HasSufficientRangeBars(int actualCount, out int expected, out int required, double thresholdPercent = 0.85)
    {
        var rangeDurationMinutes = (SlotTimeChicagoTime - RangeStartChicagoTime).TotalMinutes;
        expected = Math.Max(0, (int)Math.Floor(rangeDurationMinutes));
        required = expected > 0 ? (int)Math.Ceiling(expected * thresholdPercent) : 0;
        return actualCount >= required;
    }
    
    /// <summary>
    /// Check if bars have gaps greater than the specified maximum gap.
    /// </summary>
    private bool HasGaps(List<(Bar Bar, DateTimeOffset OpenChicago)> bars, TimeSpan maxGap)
    {
        if (bars.Count < 2) return false;
        
        for (int i = 0; i < bars.Count - 1; i++)
        {
            var gap = bars[i + 1].OpenChicago - bars[i].OpenChicago;
            if (gap > maxGap) return true;
        }
        return false;
    }
    
    /// <summary>
    /// REFACTORED: Compute range retrospectively from completed session data.
    /// Queries all bars in [RangeStartChicago, endTimeChicago) and computes range in one pass.
    /// CRITICAL: Bar filtering uses Chicago time, not UTC, to match trading session semantics.
    /// </summary>
    private (bool Success, decimal? RangeHigh, decimal? RangeLow, decimal? FreezeClose, string FreezeCloseSource, int BarCount, string? Reason, DateTimeOffset? FirstBarUtc, DateTimeOffset? LastBarUtc, DateTimeOffset? FirstBarChicago, DateTimeOffset? LastBarChicago) ComputeRangeRetrospectively(DateTimeOffset utcNow, DateTimeOffset? endTimeUtc = null)
    {
        var bars = new List<Bar>();
        
        // Determine end time for range computation (default to slot_time, but can be current time for hybrid init)
        var endTimeUtcActual = endTimeUtc ?? SlotTimeUtc;
        var endTimeChicagoActual = _time.ConvertUtcToChicago(endTimeUtcActual);
        
        // Use bar buffer for range computation (bars from pre-hydration + OnBar())
        bars.AddRange(GetBarBufferSnapshot());

        // CRITICAL: Filter bars using Chicago time, not UTC
        // Convert each bar timestamp to Chicago time explicitly
        // OPTIMIZATION: Store Chicago time with bar to avoid redundant conversion in freeze close loop
        var filteredBars = new List<Bar>();
        var barChicagoTimes = new Dictionary<Bar, DateTimeOffset>(); // Cache Chicago time per bar
        DateTimeOffset? firstBarRawUtc = null;
        DateTimeOffset? lastBarRawUtc = null;
        DateTimeOffset? firstBarChicago = null;
        DateTimeOffset? lastBarChicago = null;
        string? firstBarRawUtcKind = null;
        string? lastBarRawUtcKind = null;
        
        // Get expected trading date from journal (should match timetable)
        var expectedTradingDate = TradingDate; // Format: "YYYY-MM-DD"
        
        // Track filtering statistics for diagnostics
        int barsFilteredByDate = 0;
        int barsFilteredByTimeWindow = 0;
        int barsAccepted = 0;
        DateTimeOffset? firstFilteredBarUtc = null;
        DateTimeOffset? lastFilteredBarUtc = null;
        string? firstFilteredBarReason = null;
        
        foreach (var bar in bars)
        {
            // Capture raw timestamp as received (assumed UTC)
            var barRawUtc = bar.TimestampUtc;
            var barRawUtcKind = barRawUtc.DateTime.Kind.ToString();
            
            // Convert to Chicago time for filtering
            var barChicagoTime = _time.ConvertUtcToChicago(barRawUtc);
            
            // CRITICAL: Filter by trading date first - only process bars from the correct trading date
            var barTradingDate = _time.GetChicagoDateToday(barRawUtc).ToString("yyyy-MM-dd");
            if (barTradingDate != expectedTradingDate)
            {
                // Bar is from wrong trading date - skip it
                // This ensures we only compute ranges from bars matching the timetable trading date
                barsFilteredByDate++;
                if (firstFilteredBarUtc == null)
                {
                    firstFilteredBarUtc = barRawUtc;
                    firstFilteredBarReason = $"Date mismatch: bar date {barTradingDate} != expected {expectedTradingDate}";
                }
                lastFilteredBarUtc = barRawUtc;
                continue;
            }
            
            // Range window is defined in Chicago time: [RangeStartChicagoTime, endTimeChicagoActual)
            // For hybrid initialization, endTime can be current time (not just slot_time)
            
            // DIAGNOSTIC: Proof log for 1-minute boundary investigation (retrospective computation)
            var comparisonResultRetro = barChicagoTime >= RangeStartChicagoTime && barChicagoTime < endTimeChicagoActual;
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "BAR_ADMISSION_PROOF_RETROSPECTIVE", State.ToString(),
                new
                {
                    bar_time_raw_utc = barRawUtc.ToString("o"),
                    bar_time_raw_kind = barRawUtcKind,
                    bar_time_chicago = barChicagoTime.ToString("o"),
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    range_end_chicago = endTimeChicagoActual.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                    comparison_result = comparisonResultRetro,
                    comparison_detail = comparisonResultRetro
                        ? $"bar_chicago ({barChicagoTime:HH:mm:ss}) >= range_start ({RangeStartChicagoTime:HH:mm:ss}) AND bar_chicago < end_time ({endTimeChicagoActual:HH:mm:ss})"
                        : $"bar_chicago ({barChicagoTime:HH:mm:ss}) NOT in [range_start ({RangeStartChicagoTime:HH:mm:ss}), end_time ({endTimeChicagoActual:HH:mm:ss}))",
                    bar_source = "CSV",
                    note = "Diagnostic proof log - bar timestamps represent OPEN time (CSV bars from translator already use open time)"
                }));
            
            if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < endTimeChicagoActual)
            {
                filteredBars.Add(bar);
                barChicagoTimes[bar] = barChicagoTime; // Cache Chicago time for reuse
                barsAccepted++;
                
                // Capture diagnostic info for first and last filtered bars
                if (firstBarRawUtc == null)
                {
                    firstBarRawUtc = barRawUtc;
                    firstBarRawUtcKind = barRawUtcKind;
                    firstBarChicago = barChicagoTime;
                }
                lastBarRawUtc = barRawUtc;
                lastBarRawUtcKind = barRawUtcKind;
                lastBarChicago = barChicagoTime;
            }
            else
            {
                // Bar is from correct date but outside time window
                barsFilteredByTimeWindow++;
                if (firstFilteredBarUtc == null && barsFilteredByDate == 0)
                {
                    firstFilteredBarUtc = barRawUtc;
                    var barTimeStr = barChicagoTime.ToString("HH:mm:ss");
                    var rangeStartStr = RangeStartChicagoTime.ToString("HH:mm:ss");
                    var rangeEndStr = endTimeChicagoActual.ToString("HH:mm:ss");
                    firstFilteredBarReason = $"Time window: bar time {barTimeStr} not in [{rangeStartStr}, {rangeEndStr})";
                }
                lastFilteredBarUtc = barRawUtc;
            }
        }
        
        // Log bar filtering details if bars were filtered out (rate-limited, diagnostic only)
        if (_enableDiagnosticLogs && (barsFilteredByDate > 0 || barsFilteredByTimeWindow > 0))
        {
            var shouldLogFiltering = !_lastBarFilteringLogUtc.HasValue || 
                                    (utcNow - _lastBarFilteringLogUtc.Value).TotalMinutes >= 1.0;
            if (shouldLogFiltering)
            {
                _lastBarFilteringLogUtc = utcNow;
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "RANGE_COMPUTE_BAR_FILTERING", State.ToString(),
                    new
                    {
                        bars_in_buffer = bars.Count,
                        bars_accepted = barsAccepted,
                        bars_filtered_by_date = barsFilteredByDate,
                        bars_filtered_by_time_window = barsFilteredByTimeWindow,
                        expected_trading_date = expectedTradingDate,
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        range_end_chicago = endTimeChicagoActual.ToString("o"),
                        first_filtered_bar_utc = firstFilteredBarUtc?.ToString("o"),
                        last_filtered_bar_utc = lastFilteredBarUtc?.ToString("o"),
                        first_filtered_bar_reason = firstFilteredBarReason,
                        note = $"Bar filtering details: {barsAccepted} accepted, {barsFilteredByDate} filtered by date, {barsFilteredByTimeWindow} filtered by time window"
                    }));
            }
        }
        
        bars = filteredBars;

        if (bars.Count == 0)
        {
            // Diagnostic: Log detailed information when no bars found in range window
            // This helps diagnose date mismatch or timing issues
            var barBufferCount = 0;
            DateTimeOffset? firstBarInBufferUtc = null;
            DateTimeOffset? lastBarInBufferUtc = null;
            DateTimeOffset? firstBarInBufferChicago = null;
            DateTimeOffset? lastBarInBufferChicago = null;
            string? barBufferDateRange = null;
            
            var bufferSnapshot = GetBarBufferSnapshot();
            barBufferCount = bufferSnapshot.Count;
            if (bufferSnapshot.Count > 0)
            {
                firstBarInBufferUtc = bufferSnapshot[0].TimestampUtc;
                lastBarInBufferUtc = bufferSnapshot[bufferSnapshot.Count - 1].TimestampUtc;
                firstBarInBufferChicago = _time.ConvertUtcToChicago(firstBarInBufferUtc.Value);
                lastBarInBufferChicago = _time.ConvertUtcToChicago(lastBarInBufferUtc.Value);
                
                var firstDate = _time.GetChicagoDateToday(firstBarInBufferUtc.Value);
                var lastDate = _time.GetChicagoDateToday(lastBarInBufferUtc.Value);
                if (firstDate == lastDate)
                {
                    barBufferDateRange = TimeService.FormatDateOnly(firstDate);
                }
                else
                {
                    barBufferDateRange = $"{TimeService.FormatDateOnly(firstDate)} to {TimeService.FormatDateOnly(lastDate)}";
                }
            }
            
            // Determine if bars exist but are from wrong trading date
            var barsFromWrongDate = false;
            var barsFromCorrectDate = false;
            if (barBufferCount > 0 && firstBarInBufferUtc.HasValue)
            {
                var firstBarDate = _time.GetChicagoDateToday(firstBarInBufferUtc.Value).ToString("yyyy-MM-dd");
                barsFromWrongDate = (firstBarDate != expectedTradingDate);
                barsFromCorrectDate = (firstBarDate == expectedTradingDate);
            }
            
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_COMPUTE_NO_BARS_DIAGNOSTIC", State.ToString(),
                new
                {
                    trading_date = TradingDate,
                    expected_trading_date = expectedTradingDate,
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    range_start_utc = RangeStartUtc.ToString("o"),
                    range_end_chicago = endTimeChicagoActual.ToString("o"),
                    range_end_utc = endTimeUtcActual.ToString("o"),
                    first_bar_timestamp_chicago = firstBarInBufferChicago?.ToString("o"),
                    first_bar_timestamp_utc = firstBarInBufferUtc?.ToString("o"),
                    last_bar_timestamp_chicago = lastBarInBufferChicago?.ToString("o"),
                    last_bar_timestamp_utc = lastBarInBufferUtc?.ToString("o"),
                    bar_buffer_count = barBufferCount,
                    bar_buffer_date_range = barBufferDateRange ?? "NO_BARS",
                    bars_from_wrong_date = barsFromWrongDate,
                    bars_from_correct_date = barsFromCorrectDate,
                    note = barsFromWrongDate 
                        ? $"No bars found in range window - bars in buffer are from different trading date ({barBufferDateRange}). Waiting for bars from {expectedTradingDate}."
                        : "No bars found in range window - waiting for bars from correct trading date or check date alignment"
                }));
            
            // Determine more specific reason code
            string reasonCode;
            if (barBufferCount == 0)
            {
                reasonCode = "NO_BARS_YET";
            }
            else if (barsFromWrongDate)
            {
                reasonCode = "BARS_FROM_WRONG_DATE";
            }
            else if (barsFromCorrectDate)
            {
                reasonCode = "OUTSIDE_RANGE_WINDOW";
            }
            else
            {
                reasonCode = "NO_BARS_IN_WINDOW";
            }
            
            return (false, null, null, null, "UNSET", 0, reasonCode, null, null, null, null);
        }

        // Sort by timestamp (should already be sorted, but ensure correctness)
        bars.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        
        // DIAGNOSTIC: Log RANGE_WINDOW_AUDIT with timestamp conversion details (only if diagnostic logs enabled)
        if (_enableDiagnosticLogs && firstBarRawUtc.HasValue && lastBarRawUtc.HasValue)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_WINDOW_AUDIT", State.ToString(),
                new
                {
                    first_bar_chicago = firstBarChicago?.ToString("o"),
                    last_bar_chicago = lastBarChicago?.ToString("o"),
                    bar_count = bars.Count,
                    conversion_method = "ConvertUtcToChicago",
                    conversion_note = "Assumes bar.TimestampUtc is UTC (as received from NinjaTrader after conversion)"
                }));
        }

        // Compute range in one pass
        decimal rangeHigh = bars[0].High;
        decimal rangeLow = bars[0].Low;
        decimal? freezeClose = null;
        DateTimeOffset? lastBarBeforeSlotUtc = null;
        DateTimeOffset? lastBarBeforeSlotChicago = null;

        foreach (var bar in bars)
        {
            rangeHigh = Math.Max(rangeHigh, bar.High);
            rangeLow = Math.Min(rangeLow, bar.Low);
            
            // Find last bar close before end time (for freeze close)
            // Use Chicago time comparison to match trading session semantics
            // OPTIMIZATION: Reuse cached Chicago time instead of converting again
            var barChicagoTime = barChicagoTimes[bar];
            if (barChicagoTime < endTimeChicagoActual)
            {
                freezeClose = bar.Close;
                lastBarBeforeSlotUtc = bar.TimestampUtc;
                lastBarBeforeSlotChicago = barChicagoTime;
            }
        }

        // Validate range was computed successfully
        if (rangeHigh < rangeLow)
        {
            return (false, null, null, null, "UNSET", bars.Count, "INVALID_RANGE_HIGH_LOW", bars[0].TimestampUtc, bars[bars.Count - 1].TimestampUtc, _time.ConvertUtcToChicago(bars[0].TimestampUtc), _time.ConvertUtcToChicago(bars[bars.Count - 1].TimestampUtc));
        }

        // Validate we have sufficient bars for reliable range computation
        if (bars.Count < 3)
        {
            return (false, null, null, null, "UNSET", bars.Count, "INSUFFICIENT_BARS", bars[0].TimestampUtc, bars[bars.Count - 1].TimestampUtc, _time.ConvertUtcToChicago(bars[0].TimestampUtc), _time.ConvertUtcToChicago(bars[bars.Count - 1].TimestampUtc));
        }

        // Validate we have a freeze close
        if (!freezeClose.HasValue || !lastBarBeforeSlotUtc.HasValue)
        {
            return (false, null, null, null, "UNSET", bars.Count, "NO_FREEZE_CLOSE", bars[0].TimestampUtc, bars[bars.Count - 1].TimestampUtc, _time.ConvertUtcToChicago(bars[0].TimestampUtc), _time.ConvertUtcToChicago(bars[bars.Count - 1].TimestampUtc));
        }

        // CRITICAL: Validate timezone edge cases (DST, holidays, early closes)
        // Timezone edge case problems:
        // - DST transitions: Missing hour (spring forward) or duplicate hour (fall back)
        // - Holidays: Early closes, missing sessions
        // - What breaks: BarsRequest window wrong, live bars appear "in future", range windows misalign
        //
        // Mitigation: Validate expected vs actual bar count and session length
        var sessionDurationMinutes = (endTimeChicagoActual - RangeStartChicagoTime).TotalMinutes;
        var expectedBarCount = (int)Math.Round(sessionDurationMinutes); // 1-minute bars
        var actualBarCount = bars.Count;
        var barCountDiff = actualBarCount - expectedBarCount;
        var barCountMismatch = Math.Abs(barCountDiff) > 5; // Allow 5 bar tolerance for gaps
        
        // Check for DST transition (offset change within session)
        var startOffset = RangeStartChicagoTime.Offset;
        var endOffset = endTimeChicagoActual.Offset;
        var dstTransitionDetected = startOffset != endOffset;
        
        // Check for session length anomaly (early close or extended session)
        var nominalSessionLengthMinutes = (SlotTimeChicagoTime - RangeStartChicagoTime).TotalMinutes;
        var actualSessionLengthMinutes = sessionDurationMinutes;
        var sessionLengthAnomaly = Math.Abs(actualSessionLengthMinutes - nominalSessionLengthMinutes) > 10; // Allow 10 min tolerance
        
        // Log timezone edge case warnings if detected
        if (barCountMismatch || dstTransitionDetected || sessionLengthAnomaly)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "TIMEZONE_EDGE_CASE_DETECTED", State.ToString(),
                new
                {
                    expected_bar_count = expectedBarCount,
                    actual_bar_count = actualBarCount,
                    bar_count_mismatch = barCountMismatch,
                    bar_count_diff = barCountDiff,
                    note_bar_count_mismatch = barCountMismatch 
                        ? "Bar count differs from expected (informational only - not an error). Possible causes: partial-minute boundaries, DST transitions, deduplication, low liquidity early closes."
                        : "Bar count matches expected (within tolerance)",
                    nominal_session_length_minutes = nominalSessionLengthMinutes,
                    actual_session_length_minutes = actualSessionLengthMinutes,
                    session_length_anomaly = sessionLengthAnomaly,
                    session_length_diff_minutes = actualSessionLengthMinutes - nominalSessionLengthMinutes,
                    dst_transition_detected = dstTransitionDetected,
                    start_offset = startOffset.ToString(),
                    end_offset = endOffset.ToString(),
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    range_end_chicago = endTimeChicagoActual.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                    note = dstTransitionDetected 
                        ? "DST transition detected - missing or duplicate hour may affect bar count"
                        : sessionLengthAnomaly
                            ? "Session length anomaly detected - possible early close or extended session"
                            : "Bar count mismatch - possible DST transition, holiday, or data gap"
                }));
        }
        
        // Return first and last bar timestamps in both UTC and Chicago time for auditability
        var firstBarUtc = bars[0].TimestampUtc;
        var lastBarUtc = bars[bars.Count - 1].TimestampUtc;
        var firstBarChicagoResult = _time.ConvertUtcToChicago(firstBarUtc);
        var lastBarChicagoResult = _time.ConvertUtcToChicago(lastBarUtc);

        return (true, rangeHigh, rangeLow, freezeClose, "BAR_CLOSE", bars.Count, null, firstBarUtc, lastBarUtc, firstBarChicagoResult, lastBarChicagoResult);
    }

    /// <summary>
    /// SINGLE AUTHORITATIVE METHOD: Finalize and lock range for this stream + trading day.
    /// This is the ONLY place ranges can be committed and RANGE_LOCKED state transition can occur.
    /// Returns true if range was already locked or successfully locked, false if locking failed.
    /// </summary>
    private bool TryLockRange(DateTimeOffset utcNow)
    {
        // Already locked - idempotent return
        if (_rangeLocked)
            return true;
        
        // GUARD: Check if BarsRequest is still pending for this instrument
        // Prevents range lock before BarsRequest completes (avoids locking with insufficient bars)
        // CRITICAL: Check both CanonicalInstrument and ExecutionInstrument
        // BarsRequest might be marked pending with either one
        if (IsSimMode() && _engine != null)
        {
            var isPending = _engine.IsBarsRequestPending(CanonicalInstrument, utcNow) ||
                           _engine.IsBarsRequestPending(ExecutionInstrument, utcNow);
            
            if (isPending)
            {
                // BarsRequest is still pending - wait for it to complete
                // Log rate-limited warning (once per minute max)
                var shouldLog = !_lastRangeComputeFailedLogUtc.HasValue || 
                               (utcNow - _lastRangeComputeFailedLogUtc.Value).TotalMinutes >= 1.0;
                
                if (shouldLog)
                {
                    _lastRangeComputeFailedLogUtc = utcNow;
                    LogHealth("WARN", "RANGE_LOCK_BLOCKED_BARSREQUEST_PENDING",
                        "Range lock blocked - BarsRequest is still pending for this instrument",
                        new
                        {
                            execution_instrument = ExecutionInstrument,
                            canonical_instrument = CanonicalInstrument,
                            canonical_pending = _engine.IsBarsRequestPending(CanonicalInstrument, utcNow),
                            execution_pending = _engine.IsBarsRequestPending(ExecutionInstrument, utcNow),
                            slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                            current_time_chicago = _time.ConvertUtcToChicago(utcNow).ToString("o"),
                            note = "Range lock will proceed once BarsRequest completes or times out (5 minutes)"
                        });
                }
                
                // Return false to retry on next tick
                return false;
            }
        }
        
        // Compute final range from all available bars
        var rangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: SlotTimeUtc);
        
        if (!rangeResult.Success)
        {
            // Log failure but don't throw - caller handles retry logic
            LogHealth("WARN", "RANGE_LOCK_FAILED", 
                $"Failed to compute final range for locking",
                new
                {
                    reason = rangeResult.Reason ?? "UNKNOWN",
                    bar_count = rangeResult.BarCount,
                    slot_time_utc = SlotTimeUtc.ToString("o"),
                    utc_now = utcNow.ToString("o")
                });
            _rangeLockAttemptedAtUtc = utcNow;
            _rangeLockFailureCount++;
            return false;
        }
        
        // VALIDATION: Ensure range was properly computed before locking
        // Check 1: Range values must be present
        if (!rangeResult.RangeHigh.HasValue || !rangeResult.RangeLow.HasValue || !rangeResult.FreezeClose.HasValue)
        {
            LogHealth("CRITICAL", "RANGE_LOCK_VALIDATION_FAILED",
                "Cannot lock range - range values are missing",
                new
                {
                    range_high_has_value = rangeResult.RangeHigh.HasValue,
                    range_low_has_value = rangeResult.RangeLow.HasValue,
                    freeze_close_has_value = rangeResult.FreezeClose.HasValue,
                    bar_count = rangeResult.BarCount,
                    reason = rangeResult.Reason
                });
            _rangeLockAttemptedAtUtc = utcNow;
            _rangeLockFailureCount++;
            return false;
        }

        // Check 2: Range high must be greater than range low (sanity check)
        if (rangeResult.RangeHigh.Value <= rangeResult.RangeLow.Value)
        {
            LogHealth("CRITICAL", "RANGE_LOCK_VALIDATION_FAILED",
                "Cannot lock range - range high is not greater than range low",
                new
                {
                    range_high = rangeResult.RangeHigh.Value,
                    range_low = rangeResult.RangeLow.Value,
                    bar_count = rangeResult.BarCount,
                    note = "Invalid range values - high must be > low"
                });
            _rangeLockAttemptedAtUtc = utcNow;
            _rangeLockFailureCount++;
            return false;
        }

        // Check 3: Must have bars in buffer (range was computed from actual data)
        if (rangeResult.BarCount == 0)
        {
            LogHealth("CRITICAL", "RANGE_LOCK_VALIDATION_FAILED",
                "Cannot lock range - no bars were used in computation",
                new
                {
                    bar_count = rangeResult.BarCount,
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                    note = "Range computation returned 0 bars - cannot lock without data"
                });
            _rangeLockAttemptedAtUtc = utcNow;
            _rangeLockFailureCount++;
            return false;
        }

        // All validation passed - log validation success for auditability
        LogHealth("INFO", "RANGE_LOCK_VALIDATION_PASSED",
            "Range validation passed - proceeding with lock",
            new
            {
                range_high = rangeResult.RangeHigh.Value,
                range_low = rangeResult.RangeLow.Value,
                freeze_close = rangeResult.FreezeClose.Value,
                bar_count = rangeResult.BarCount,
                first_bar_chicago = rangeResult.FirstBarChicago?.ToString("o"),
                last_bar_chicago = rangeResult.LastBarChicago?.ToString("o")
            });
        
        // CRITICAL: Atomic commit - set all values together
        RangeHigh = rangeResult.RangeHigh;
        RangeLow = rangeResult.RangeLow;
        FreezeClose = rangeResult.FreezeClose;
        FreezeCloseSource = rangeResult.FreezeCloseSource;
        
        // ============================================================
        // PHASE A: ATOMIC LOCK (no side effects)
        // ============================================================
        
        // 1. ComputeRangeRetrospectively(endTimeUtc = SlotTimeUtc)
        // (Already computed above)
        
        // 2. Commit RangeHigh/Low/FreezeClose/Source
        // (Already committed above)
        
        // 3. Attempt breakout derivation
        ComputeBreakoutLevelsAndLog(utcNow);
        
        // REQUIRED CHANGE #3: Do NOT block locking if derivation fails
        // Remove: "If breakout levels missing => return false"
        // Replace with: Lock the range anyway, log RANGE_LOCKED_DERIVATION_FAILED, apply trading gate
        // Otherwise a rounding/tick bug can prevent locking forever.
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
        {
            // Set gate flag to prevent entries until breakout levels exist
            _breakoutLevelsMissing = true;
            
            LogHealth("WARN", "RANGE_LOCKED_DERIVATION_FAILED", 
                "Range locked successfully but breakout levels not computed - stream blocked from entry until resolved",
                new
                {
                    range_high = RangeHigh,
                    range_low = RangeLow,
                    brk_long_has_value = _brkLongRounded.HasValue,
                    brk_short_has_value = _brkShortRounded.HasValue,
                    gate_flag_set = _breakoutLevelsMissing,
                    note = "Range lock succeeded; breakout levels are derived and will be enforced as separate trading gate"
                });
        }
        else
        {
            // Breakout levels computed successfully - clear gate flag
            _breakoutLevelsMissing = false;
        }
        
        // 4. Set _rangeLocked = true
        // CRITICAL: This is the point of no return - after this, range is immutable
        _rangeLocked = true;
        
        // 5. Transition to RANGE_LOCKED
        // CRITICAL: This completes Phase A - range is now locked and state is RANGE_LOCKED
        var rangeLogData = CreateRangeLogData(RangeHigh, RangeLow, FreezeClose, FreezeCloseSource);
        Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED", rangeLogData);
        
        // Mark lock as committed (for duplicate detection)
        _rangeLockCommitted = true;
        
        // Reset failure count on success
        _rangeLockFailureCount = 0;
        
        // ============================================================
        // PHASE B: BEST-EFFORT SIDE EFFECTS (failures don't affect lock)
        // ============================================================
        // REQUIRED CHANGE #2: Phase B must be wrapped so failures cannot strand the stream in a half-locked state.
        
        try
        {
            // 1. EmitRangeLockedEvents
            EmitRangeLockedEvents(utcNow, rangeResult);
            
            // 2. LogSlotEndSummary
            LogSlotEndSummary(utcNow, "RANGE_VALID", true, false, "Range locked, awaiting signal");
            
            // 3. CheckImmediateEntryAtLock (gate on breakout levels)
            if (FreezeClose.HasValue && RangeHigh.HasValue && RangeLow.HasValue && !_breakoutLevelsMissing)
            {
                CheckImmediateEntryAtLock(utcNow);
            }
            
            // 4. SubmitStopEntryBracketsAtLock (gate on breakout levels)
            if (!_entryDetected && utcNow < MarketCloseUtc && !_breakoutLevelsMissing)
            {
                SubmitStopEntryBracketsAtLock(utcNow);
            }
            else if (_breakoutLevelsMissing)
            {
                // Log why brackets weren't submitted
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "STOP_BRACKETS_BLOCKED_MISSING_BREAKOUTS", State.ToString(),
                    new
                    {
                        reason = "BREAKOUT_LEVELS_MISSING",
                        gate_flag = _breakoutLevelsMissing,
                        note = "Brackets blocked because breakout levels failed to compute - stream gated from entry"
                    }));
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - range is locked, post-lock actions are best-effort
            // CRITICAL: Do NOT reset _rangeLocked or _rangeLockCommitted - lock is valid
            // Phase B failures cannot strand the stream in a half-locked state
            LogHealth("ERROR", "RANGE_LOCKED_POST_ACTIONS_FAILED", 
                "Failed to execute post-lock actions (range is still locked)",
                new 
                { 
                    error = ex.Message,
                    range_locked = _rangeLocked,
                    range_committed = _rangeLockCommitted,
                    note = "Lock is valid; post-lock side effects failed but don't affect lock invariant"
                });
        }
        
        return true;
    }

    /// <summary>
    /// Emit all RANGE_LOCKED events (RangeLockedEvent and HydrationEvent).
    /// Called ONLY from TryLockRange to ensure single emission point.
    /// </summary>
    private void EmitRangeLockedEvents(DateTimeOffset utcNow, (bool Success, decimal? RangeHigh, decimal? RangeLow, decimal? FreezeClose, string FreezeCloseSource, int BarCount, string? Reason, DateTimeOffset? FirstBarUtc, DateTimeOffset? LastBarUtc, DateTimeOffset? FirstBarChicago, DateTimeOffset? LastBarChicago) rangeResult)
    {
        if (!RangeHigh.HasValue || !RangeLow.HasValue || !FreezeClose.HasValue)
            return;
        
        try
        {
            // Create and persist canonical RANGE_LOCKED event
            var rangeLockedEvent = new RangeLockedEvent(
                tradingDay: TradingDate,
                streamId: Stream,
                canonicalInstrument: CanonicalInstrument,
                executionInstrument: ExecutionInstrument,
                rangeHigh: RangeHigh.Value,
                rangeLow: RangeLow.Value,
                rangeSize: RangeHigh.Value - RangeLow.Value,
                freezeClose: FreezeClose.Value,
                rangeHighRounded: RangeHigh.Value,
                rangeLowRounded: RangeLow.Value,
                breakoutLong: _brkLongRounded ?? 0m, // Use 0 if not computed (persister will handle)
                breakoutShort: _brkShortRounded ?? 0m,
                rangeStartTimeChicago: RangeStartChicagoTime.ToString("o"),
                rangeStartTimeUtc: RangeStartUtc.ToString("o"),
                rangeEndTimeChicago: SlotTimeChicagoTime.ToString("o"),
                rangeEndTimeUtc: SlotTimeUtc.ToString("o"),
                lockedAtChicago: _time.ConvertUtcToChicago(utcNow).ToString("o"),
                lockedAtUtc: utcNow.ToString("o")
            );
            
            _rangePersister?.Persist(rangeLockedEvent);
            
            // Also log to hydration file
            var chicagoNow = _time.ConvertUtcToChicago(utcNow);
            var barCount = GetBarBufferCount();
            var rangeLockedData = new Dictionary<string, object>
            {
                ["range_high"] = RangeHigh.Value,
                ["range_low"] = RangeLow.Value,
                ["range_size"] = RangeHigh.Value - RangeLow.Value,
                ["freeze_close"] = FreezeClose.Value,
                ["breakout_long"] = _brkLongRounded.HasValue ? _brkLongRounded.Value : (decimal?)null,
                ["breakout_short"] = _brkShortRounded.HasValue ? _brkShortRounded.Value : (decimal?)null,
                ["range_start_time_chicago"] = RangeStartChicagoTime.ToString("o"),
                ["range_end_time_chicago"] = SlotTimeChicagoTime.ToString("o"),
                ["range_start_time_utc"] = RangeStartUtc.ToString("o"),
                ["range_end_time_utc"] = SlotTimeUtc.ToString("o"),
                ["bar_count"] = barCount,
                ["tick_size"] = _tickSize,
                ["source"] = "final",
                ["breakout_levels_missing"] = _breakoutLevelsMissing
            };
            
            var hydrationEvent = new HydrationEvent(
                eventType: "RANGE_LOCKED",
                tradingDay: TradingDate,
                streamId: Stream,
                canonicalInstrument: CanonicalInstrument,
                executionInstrument: ExecutionInstrument,
                session: Session,
                slotTimeChicago: SlotTimeChicago,
                timestampUtc: utcNow,
                timestampChicago: chicagoNow,
                state: State.ToString(),
                data: rangeLockedData
            );
            
            _hydrationPersister?.Persist(hydrationEvent);
            
            // HARD ASSERTION: Check for duplicate RANGE_LOCKED events
            // This should never happen if TryLockRange is the only entry point
            if (_rangeLockAssertEmitted)
            {
                LogHealth("CRITICAL", "DUPLICATE_RANGE_LOCKED", 
                    "RANGE_LOCKED event emitted more than once per stream per trading day - CRITICAL ERROR",
                    new
                    {
                        stream_id = Stream,
                        trading_date = TradingDate,
                        violation = "DUPLICATE_RANGE_LOCKED_EVENT",
                        note = "This indicates a logic bug - TryLockRange should be the only entry point"
                    });
            }
            _rangeLockAssertEmitted = true;
        }
        catch (Exception ex)
        {
            LogHealth("ERROR", "RANGE_LOCKED_EVENT_EMIT_FAILED", 
                "Failed to emit RANGE_LOCKED events",
                new { error = ex.Message, note = "Range is locked but events failed - execution continues" });
        }
    }

    /// <summary>
    /// Restore locked range state from canonical hydration/ranges log.
    /// REQUIRED CHANGE #4: On startup, replay hydration_{day}.jsonl (or ranges_{day}.jsonl).
    /// If a RANGE_LOCKED event exists for this stream+day, restore locked state.
    /// </summary>
    private void RestoreRangeLockedFromHydrationLog(string tradingDay, string streamId)
    {
        try
        {
            // Try hydration log first
            var hydrationFile = Path.Combine(_projectRoot, "logs", "robot", $"hydration_{tradingDay}.jsonl");
            var usingRangesFile = false;
            if (!File.Exists(hydrationFile))
            {
                // Fallback to ranges file
                hydrationFile = Path.Combine(_projectRoot, "logs", "robot", $"ranges_{tradingDay}.jsonl");
                usingRangesFile = true;
                if (!File.Exists(hydrationFile))
                {
                    LogHealth("WARN", "RANGE_LOCKED_RESTORE_FILE_MISSING", 
                        "Neither hydration nor ranges log file found for restoration",
                        new
                        {
                            trading_day = tradingDay,
                            stream_id = streamId,
                            hydration_file = Path.Combine(_projectRoot, "logs", "robot", $"hydration_{tradingDay}.jsonl"),
                            ranges_file = Path.Combine(_projectRoot, "logs", "robot", $"ranges_{tradingDay}.jsonl"),
                            note = "Will proceed without restoration - range will be recomputed"
                        });
                    return;
                }
            }
            
            // Diagnostic: Log which file we're using
            LogHealth("INFO", "RANGE_LOCKED_RESTORE_ATTEMPT", 
                "Attempting to restore range lock from log file",
                new
                {
                    trading_day = tradingDay,
                    stream_id = streamId,
                    file_path = hydrationFile,
                    file_type = usingRangesFile ? "ranges" : "hydration",
                    file_exists = File.Exists(hydrationFile)
                });
            
            // Read hydration/ranges log and find RANGE_LOCKED event for this stream
            // IMPORTANT: Find the MOST RECENT event (last one in file), not the first
            var lines = File.ReadAllLines(hydrationFile);
            Dictionary<string, object>? latestHydrationData = null; // Store data dict directly (avoids HydrationEvent deserialization issues)
            RangeLockedEvent? latestRangeEvt = null;
            
            // Scan all lines to find the most recent matching event
            // CRITICAL: Check JSON structure first to determine which type to deserialize
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                try
                {
                    // Quick check: Does this line contain RANGE_LOCKED event for our stream?
                    // This avoids expensive deserialization attempts on every line
                    if (!line.Contains($"\"stream_id\":\"{streamId}\"") || 
                        !line.Contains($"\"trading_day\":\"{tradingDay}\"") ||
                        !line.Contains("RANGE_LOCKED"))
                    {
                        continue; // Skip lines that can't possibly match
                    }
                    
                    // Determine format: hydration log has "data" dictionary, ranges log has flat structure
                    bool looksLikeHydrationFormat = line.Contains("\"data\":{");
                    bool looksLikeRangesFormat = line.Contains("\"range_high\":") && !line.Contains("\"data\":");
                    
                    // Try HydrationEvent first (hydration log format)
                    // CRITICAL: HydrationEvent has constructor-only properties, so deserialization might fail
                    // Instead, parse JSON manually to extract fields
                    if (looksLikeHydrationFormat)
                    {
                        try
                        {
                            // Parse as dictionary first to avoid constructor-only property issues
                            var dict = JsonUtil.Deserialize<Dictionary<string, object>>(line);
                            if (dict != null &&
                                dict.TryGetValue("event_type", out var evtType) && evtType?.ToString() == "RANGE_LOCKED" &&
                                dict.TryGetValue("trading_day", out var td) && td?.ToString() == tradingDay &&
                                dict.TryGetValue("stream_id", out var sid) && sid?.ToString() == streamId &&
                                dict.TryGetValue("slot_time_chicago", out var stc) && stc?.ToString() == SlotTimeChicago)
                            {
                                // Extract data dictionary - handle different deserializer types
                                Dictionary<string, object>? dataDict = null;
                                if (dict.TryGetValue("data", out var dataObj))
                                {
                                    // JavaScriptSerializer returns Dictionary<string, object> directly
                                    if (dataObj is Dictionary<string, object> directDict)
                                    {
                                        dataDict = directDict;
                                    }
                                    // System.Text.Json might wrap it differently - try to convert
                                    else if (dataObj != null)
                                    {
                                        // Try to convert to Dictionary<string, object>
                                        try
                                        {
                                            var jsonStr = JsonUtil.Serialize(dataObj);
                                            dataDict = JsonUtil.Deserialize<Dictionary<string, object>>(jsonStr);
                                        }
                                        catch (Exception convertEx)
                                        {
                                            // Log conversion failure for debugging (only first few times)
                                            if (lines.Length < 100)
                                            {
                                                LogHealth("DEBUG", "RANGE_LOCKED_RESTORE_DATA_CONVERT_FAILED", 
                                                    "Failed to convert data object to dictionary",
                                                    new
                                                    {
                                                        trading_day = tradingDay,
                                                        stream_id = streamId,
                                                        data_obj_type = dataObj.GetType().Name,
                                                        error = convertEx.Message
                                                    });
                                            }
                                            // Conversion failed, skip
                                        }
                                    }
                                }
                                
                                if (dataDict != null)
                                {
                                    // Store data dictionary directly - we'll extract values from it during restoration
                                    latestHydrationData = dataDict;
                                }
                                else if (lines.Length < 100)
                                {
                                    // Log why we didn't find data dict (only for small files during testing)
                                    LogHealth("DEBUG", "RANGE_LOCKED_RESTORE_DATA_MISSING", 
                                        "Data dictionary not found or not extractable",
                                        new
                                        {
                                            trading_day = tradingDay,
                                            stream_id = streamId,
                                            has_data_obj = dict.TryGetValue("data", out _),
                                            data_obj_type = dict.TryGetValue("data", out var dobj) ? dobj?.GetType().Name : "null"
                                        });
                                }
                            }
                        }
                        catch
                        {
                            // Deserialization failed - might be malformed, skip
                        }
                    }
                    
                    // Try RangeLockedEvent (from ranges file format)
                    if (looksLikeRangesFormat)
                    {
                        try
                        {
                            var rangeEvt = JsonUtil.Deserialize<RangeLockedEvent>(line);
                            if (rangeEvt != null && 
                                rangeEvt.trading_day == tradingDay && 
                                rangeEvt.stream_id == streamId)
                                // Note: RangeLockedEvent doesn't have slot_time_chicago field, so we can't match by slot
                                // This is a limitation - ranges log format doesn't include slot info
                                // For now, we'll match by stream_id only for ranges log
                                // Hydration log matching above includes slot_time_chicago check
                            {
                                // Keep the most recent one (last in file)
                                latestRangeEvt = rangeEvt;
                            }
                        }
                        catch
                        {
                            // Deserialization failed - skip this line
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Skip malformed lines - log first few failures for debugging
                    // (Don't spam logs with every malformed line)
                    if (lines.Length < 100) // Only log if file is small (likely during testing)
                    {
                        LogHealth("DEBUG", "RANGE_LOCKED_RESTORE_DESERIALIZE_FAILED", 
                            "Failed to deserialize line from log file",
                            new
                            {
                                trading_day = tradingDay,
                                stream_id = streamId,
                                line_preview = line.Length > 100 ? line.Substring(0, 100) + "..." : line,
                                error = ex.Message
                            });
                    }
                    continue;
                }
            }
            
            // Diagnostic: Log what we found
            LogHealth("INFO", "RANGE_LOCKED_RESTORE_SCAN_COMPLETE", 
                "Finished scanning log file for RANGE_LOCKED events",
                new
                {
                    trading_day = tradingDay,
                    stream_id = streamId,
                    file_path = hydrationFile,
                    total_lines_scanned = lines.Length,
                    hydration_events_found = latestHydrationData != null ? 1 : 0,
                    range_events_found = latestRangeEvt != null ? 1 : 0,
                    will_restore = latestHydrationData != null || latestRangeEvt != null
                });
            
            // Restore from the most recent event found (prefer hydration log over ranges log)
            if (latestHydrationData != null)
            {
                // Helper function to extract decimal from dictionary value (handles JsonElement from System.Text.Json)
                decimal? ExtractDecimal(Dictionary<string, object> dict, string key)
                {
                    if (!dict.ContainsKey(key)) return null;
                    var value = dict[key];
                    if (value == null) return null;
                    
                    // Handle JsonElement (from System.Text.Json)
                    var jsonElementType = System.Type.GetType("System.Text.Json.JsonElement, System.Text.Json");
                    if (jsonElementType != null && jsonElementType.IsInstanceOfType(value))
                    {
                        try
                        {
                            var getDecimalMethod = jsonElementType.GetMethod("GetDecimal");
                            if (getDecimalMethod != null)
                            {
                                return (decimal?)getDecimalMethod.Invoke(value, null);
                            }
                        }
                        catch
                        {
                            // Fall through to Convert.ToDecimal
                        }
                    }
                    
                    // Handle direct decimal or string conversion
                    try
                    {
                        return Convert.ToDecimal(value);
                    }
                    catch
                    {
                        return null;
                    }
                }
                
                // Restore locked state from canonical hydration log (extract from data dictionary)
                _rangeLocked = true;
                RangeHigh = ExtractDecimal(latestHydrationData, "range_high");
                RangeLow = ExtractDecimal(latestHydrationData, "range_low");
                FreezeClose = ExtractDecimal(latestHydrationData, "freeze_close");
                
                // Restore breakout levels if present
                _brkLongRounded = ExtractDecimal(latestHydrationData, "breakout_long");
                _brkShortRounded = ExtractDecimal(latestHydrationData, "breakout_short");
                
                // Set gate flag if breakout levels are missing
                _breakoutLevelsMissing = !_brkLongRounded.HasValue || !_brkShortRounded.HasValue;
                
                // Mark lock as committed (for duplicate detection)
                _rangeLockCommitted = true;
                
                // Restore state as RANGE_LOCKED
                var utcNow = DateTimeOffset.UtcNow;
                
                // If breakout levels are missing but range is available, compute them
                if ((!_brkLongRounded.HasValue || !_brkShortRounded.HasValue) && 
                    RangeHigh.HasValue && RangeLow.HasValue)
                {
                    ComputeBreakoutLevelsAndLog(utcNow);
                }
                var rangeLogData = CreateRangeLogData(RangeHigh, RangeLow, FreezeClose, FreezeCloseSource);
                Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED_RESTORED", rangeLogData);
                
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "RANGE_LOCKED_RESTORED_FROM_HYDRATION", State.ToString(),
                    new
                    {
                        trading_date = tradingDay,
                        stream_id = streamId,
                        range_high = RangeHigh,
                        range_low = RangeLow,
                        source = "hydration_log",
                        note = "Range lock restored from canonical hydration log"
                    }));
                
                return; // Found and restored
            }
            else if (latestRangeEvt != null)
            {
                // Restore locked state from canonical ranges log
                _rangeLocked = true;
                RangeHigh = latestRangeEvt.range_high;
                RangeLow = latestRangeEvt.range_low;
                FreezeClose = latestRangeEvt.freeze_close;
                FreezeCloseSource = "RESTORED";
                
                // Restore breakout levels
                _brkLongRounded = latestRangeEvt.breakout_long;
                _brkShortRounded = latestRangeEvt.breakout_short;
                
                // Set gate flag if breakout levels are missing
                _breakoutLevelsMissing = !_brkLongRounded.HasValue || !_brkShortRounded.HasValue;
                
                // Mark lock as committed (for duplicate detection)
                _rangeLockCommitted = true;
                
                // Restore state as RANGE_LOCKED
                var utcNow = DateTimeOffset.UtcNow;
                
                // If breakout levels are missing but range is available, compute them
                if ((!_brkLongRounded.HasValue || !_brkShortRounded.HasValue) && 
                    RangeHigh.HasValue && RangeLow.HasValue)
                {
                    ComputeBreakoutLevelsAndLog(utcNow);
                }
                var rangeLogData = CreateRangeLogData(RangeHigh, RangeLow, FreezeClose, FreezeCloseSource);
                Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED_RESTORED", rangeLogData);
                
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "RANGE_LOCKED_RESTORED_FROM_RANGES", State.ToString(),
                    new
                    {
                        trading_date = tradingDay,
                        stream_id = streamId,
                        range_high = RangeHigh,
                        range_low = RangeLow,
                        source = "ranges_log",
                        note = "Range lock restored from canonical ranges log"
                    }));
                
                return; // Found and restored
            }
            else
            {
                // No events found
                LogHealth("WARN", "RANGE_LOCKED_RESTORE_NO_EVENTS", 
                    "No RANGE_LOCKED events found in log file for this stream",
                    new
                    {
                        trading_day = tradingDay,
                        stream_id = streamId,
                        file_path = hydrationFile,
                        note = "Will proceed without restoration - range will be recomputed"
                    });
            }
        }
        catch (Exception ex)
        {
            LogHealth("WARN", "RANGE_LOCKED_RESTORE_FAILED", 
                "Failed to restore range lock from hydration/ranges log",
                new 
                { 
                    trading_day = tradingDay,
                    stream_id = streamId,
                    error = ex.Message,
                    error_type = ex.GetType().Name,
                    stack_trace = ex.StackTrace,
                    note = "Will proceed without restoration" 
                });
        }
    }

    private void Commit(DateTimeOffset utcNow, string commitReason, string eventType)
    {
        if (_journal.Committed)
        {
            State = StreamState.DONE;
            return;
        }

        _journal.Committed = true;
        _journal.CommitReason = commitReason;
        _journal.EntryDetected = _entryDetected; // Ensure persisted on commit
        _journal.LastState = StreamState.DONE.ToString();
        _journal.LastUpdateUtc = utcNow.ToString("o");
        _journal.TimetableHashAtCommit = _timetableHash;

        // Determine terminal state based on commit reason and trade completion
        _journal.TerminalState = DetermineTerminalState(commitReason, utcNow);
        
        // SLOT PERSISTENCE: Set SlotStatus based on commit reason
        var previousSlotStatus = _journal.SlotStatus;
        SlotStatus newSlotStatus;
        if (commitReason == "SLOT_EXPIRED")
        {
            newSlotStatus = SlotStatus.EXPIRED;
        }
        else if (commitReason == "NO_TRADE_MARKET_CLOSE" || commitReason.Contains("NO_TRADE"))
        {
            newSlotStatus = SlotStatus.NO_TRADE;
        }
        else if (commitReason.Contains("FAILED") || commitReason.Contains("ERROR") || commitReason.Contains("CORRUPTION") || commitReason == "STREAM_STAND_DOWN")
        {
            newSlotStatus = SlotStatus.FAILED_RUNTIME;
        }
        else if (_executionJournal != null && _executionJournal.HasCompletedTradeForStream(TradingDate, Stream))
        {
            newSlotStatus = SlotStatus.COMPLETE;
        }
        else
        {
            // Default to COMPLETE if trade completed, otherwise keep current status
            newSlotStatus = SlotStatus.COMPLETE;
        }
        
        // Only set status if it's different (allows for pre-set statuses from external calls)
        // Emit SLOT_STATUS_CHANGED if status changed (handles both internal and external changes)
        if (previousSlotStatus != newSlotStatus)
        {
            _journal.SlotStatus = newSlotStatus;
            LogHealth("INFO", "SLOT_STATUS_CHANGED", $"Slot status changed: {previousSlotStatus} -> {newSlotStatus}",
                new
                {
                    previous_status = previousSlotStatus.ToString(),
                    new_status = newSlotStatus.ToString(),
                    commit_reason = commitReason,
                    slot_instance_key = _journal.SlotInstanceKey
                });
        }
        else
        {
            // Status already set to target value (likely set externally before Commit())
            // Ensure it's set (idempotent)
            _journal.SlotStatus = newSlotStatus;
        }

        _journals.Save(_journal);
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "JOURNAL_WRITTEN", StreamState.DONE.ToString(),
            new 
            { 
                committed = true, 
                commit_reason = commitReason,
                terminal_state = _journal.TerminalState?.ToString() ?? "NULL",
                slot_status = _journal.SlotStatus.ToString(),
                timetable_hash_at_commit = _timetableHash,
                execution_instrument = ExecutionInstrument,  // PHASE 3: Execution identity
                canonical_instrument = CanonicalInstrument   // PHASE 3: Canonical identity
            }));

        State = StreamState.DONE;
        // PHASE 3: Include both identities in commit event (RANGE_INVALIDATED, STREAM_STAND_DOWN, etc.)
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            eventType, State.ToString(),
            new 
            { 
                committed = true, 
                commit_reason = commitReason,
                terminal_state = _journal.TerminalState?.ToString() ?? "NULL",
                slot_status = _journal.SlotStatus.ToString(),
                execution_instrument = ExecutionInstrument,  // PHASE 3: Execution identity
                canonical_instrument = CanonicalInstrument   // PHASE 3: Canonical identity
            }));
    }

    /// <summary>
    /// Determine terminal state based on commit reason and trade completion status.
    /// </summary>
    private StreamTerminalState DetermineTerminalState(string commitReason, DateTimeOffset utcNow)
    {
        // Check if trade was completed (from execution journal)
        bool tradeCompleted = false;
        if (_executionJournal != null)
        {
            tradeCompleted = _executionJournal.HasCompletedTradeForStream(TradingDate, Stream);
        }

        // Classify based on commit reason and trade completion
        if (tradeCompleted)
        {
            return StreamTerminalState.TRADE_COMPLETED;
        }

        // Check for zero-bar hydration (distinct from generic NO_TRADE)
        // Zero-bar hydration occurs when CSV missing, BarsRequest failed, or hard timeout with 0 bars
        if (_hadZeroBarHydration && 
            (commitReason.Contains("NO_TRADE") || 
             commitReason == "NO_TRADE_MARKET_CLOSE" ||
             commitReason.Contains("PRE_HYDRATION") ||
             commitReason.Contains("TIMEOUT")))
        {
            return StreamTerminalState.ZERO_BAR_HYDRATION;
        }

        // Classify based on commit reason
        if (commitReason == "STREAM_STAND_DOWN" || 
            commitReason.Contains("FAILED") || 
            commitReason.Contains("ERROR") ||
            commitReason.Contains("CORRUPTION"))
        {
            return StreamTerminalState.FAILED_RUNTIME;
        }

        if (commitReason == "NO_TRADE_MARKET_CLOSE" || 
            commitReason == "NO_TRADE_LATE_START_MISSED_BREAKOUT" ||
            commitReason.Contains("NO_TRADE"))
        {
            return StreamTerminalState.NO_TRADE;
        }

        if (State == StreamState.SUSPENDED_DATA_INSUFFICIENT)
        {
            return StreamTerminalState.SUSPENDED_DATA;
        }

        // Default: NO_TRADE if no other classification applies
        return StreamTerminalState.NO_TRADE;
    }
    
    /// <summary>
    /// PHASE 4: Persist missing data incident record.
    /// </summary>
    private void PersistMissingDataIncident(DateTimeOffset utcNow, string incidentMessage)
    {
        try
        {
            // Get project root from journal path (journals are in projectRoot/data/execution_journals)
            var journalPath = _journals.GetJournalPath(TradingDate, Stream);
            var projectRoot = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(journalPath)));
            
            if (string.IsNullOrEmpty(projectRoot))
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "INCIDENT_PERSIST_ERROR", State.ToString(),
                    new { error = "Could not determine project root from journal path" }));
                return;
            }
            
            var incidentDir = System.IO.Path.Combine(projectRoot, "data", "execution_incidents");
            System.IO.Directory.CreateDirectory(incidentDir);
            
            var incidentPath = System.IO.Path.Combine(incidentDir, $"missing_data_{Stream}_{utcNow:yyyyMMddHHmmss}.json");
            
            var incident = new
            {
                incident_type = "NO_DATA_NO_TRADE",
                timestamp_utc = utcNow.ToString("o"),
                trading_date = TradingDate,
                stream = Stream,
                instrument = Instrument,
                session = Session,
                slot_time_chicago = SlotTimeChicago,
                slot_time_utc = SlotTimeUtc.ToString("o"),
                range_start_chicago = RangeStartChicagoTime.ToString("o"),
                range_start_utc = RangeStartUtc.ToString("o"),
                incident_message = incidentMessage,
                action_taken = "STREAM_COMMITTED_NO_TRADE"
            };
            
            var json = JsonUtil.Serialize(incident);
            System.IO.File.WriteAllText(incidentPath, json);
        }
        catch (Exception ex)
        {
            // Fail loudly: log incident persist failure as ERROR (but do not throw)
            LogCriticalError("INCIDENT_PERSIST_ERROR", ex, utcNow, new
            {
                incident_type = "NO_DATA_NO_TRADE",
                note = "Failed to persist missing data incident file"
            });
        }
    }

    private void CheckImmediateEntryAtLock(DateTimeOffset utcNow)
    {
        if (!FreezeClose.HasValue || !_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
            return;

        var freezeClose = FreezeClose.Value;
        var brkLong = _brkLongRounded.Value;
        var brkShort = _brkShortRounded.Value;

        // Analyzer logic: immediate_long = freeze_close >= brk_long, immediate_short = freeze_close <= brk_short
        bool immediateLong = freezeClose >= brkLong;
        bool immediateShort = freezeClose <= brkShort;

        if (immediateLong && immediateShort)
        {
            // Both conditions met - choose closer breakout (Analyzer's _handle_dual_immediate_entry)
            var longDistance = Math.Abs(freezeClose - brkLong);
            var shortDistance = Math.Abs(freezeClose - brkShort);
            if (longDistance <= shortDistance)
            {
                RecordIntendedEntry("Long", brkLong, SlotTimeUtc, "IMMEDIATE_AT_LOCK", utcNow);
            }
            else
            {
                RecordIntendedEntry("Short", brkShort, SlotTimeUtc, "IMMEDIATE_AT_LOCK", utcNow);
            }
        }
        else if (immediateLong)
        {
            RecordIntendedEntry("Long", brkLong, SlotTimeUtc, "IMMEDIATE_AT_LOCK", utcNow);
        }
        else if (immediateShort)
        {
            RecordIntendedEntry("Short", brkShort, SlotTimeUtc, "IMMEDIATE_AT_LOCK", utcNow);
        }
    }

    private void CheckBreakoutEntry(DateTimeOffset barUtc, decimal high, decimal low, DateTimeOffset utcNow)
    {
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
            return;

        var brkLong = _brkLongRounded.Value;
        var brkShort = _brkShortRounded.Value;

        // Analyzer logic: long triggers when bar.high >= brk_long, short triggers when bar.low <= brk_short
        bool longTrigger = high >= brkLong;
        bool shortTrigger = low <= brkShort;

        if (longTrigger && shortTrigger)
        {
            // Both trigger - choose first by timestamp (should be same bar, so choose deterministically)
            // Analyzer chooses first by timestamp, but if same bar, we choose long (documented)
            RecordIntendedEntry("Long", brkLong, barUtc, "BREAKOUT", utcNow);
        }
        else if (longTrigger)
        {
            RecordIntendedEntry("Long", brkLong, barUtc, "BREAKOUT", utcNow);
        }
        else if (shortTrigger)
        {
            RecordIntendedEntry("Short", brkShort, barUtc, "BREAKOUT", utcNow);
        }
    }

    private void RecordIntendedEntry(string direction, decimal entryPrice, DateTimeOffset entryTimeUtc, string triggerReason, DateTimeOffset utcNow)
    {
        if (_entryDetected)
        {
            // CRITICAL FIX: Log when breakout is detected but entry already exists
            // This helps debug why breakouts aren't triggering entries (e.g., if immediate entry at lock prevents later breakout entries)
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "BREAKOUT_DETECTED_ALREADY_ENTERED", State.ToString(),
                new
                {
                    detected_direction = direction,
                    detected_entry_price = entryPrice,
                    detected_entry_time_utc = entryTimeUtc.ToString("o"),
                    detected_entry_time_chicago = _time.ConvertUtcToChicago(entryTimeUtc).ToString("o"),
                    detected_trigger_reason = triggerReason,
                    existing_entry_direction = _intendedDirection,
                    existing_entry_price = _intendedEntryPrice,
                    existing_entry_time_utc = _intendedEntryTimeUtc?.ToString("o") ?? "",
                    existing_entry_time_chicago = _intendedEntryTimeUtc.HasValue ? _time.ConvertUtcToChicago(_intendedEntryTimeUtc.Value).ToString("o") : "",
                    existing_trigger_reason = _triggerReason,
                    note = "Breakout detected but entry already exists - this breakout was ignored"
                }));
            return; // Already detected
        }

        _entryDetected = true;
        _intendedDirection = direction;
        _intendedEntryPrice = entryPrice;
        _intendedEntryTimeUtc = entryTimeUtc;
        _triggerReason = triggerReason;

        // PERSIST: Save entry detection to journal
        _journal.EntryDetected = true;
        _journals.Save(_journal);

        // Compute protective orders (stop/target/BE trigger)
        ComputeAndLogProtectiveOrders(utcNow);

        // Log intended entry (always log for DRYRUN parity)
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "DRYRUN_INTENDED_ENTRY", State.ToString(),
            new
            {
                intended_trade = true,
                direction = direction,
                entry_time_utc = entryTimeUtc.ToString("o"),
                entry_time_chicago = _time.ConvertUtcToChicago(entryTimeUtc).ToString("o"),
                entry_price = entryPrice,
                trigger_reason = triggerReason
            }));

        // Build intent and execute (execution mode only determines adapter, not behavior)
        // Note: Execution adapter will validate null values - no need to check here

        // PHASE 3: Assert Instrument property is canonical (logic identity)
        if (Instrument != CanonicalInstrument)
        {
            throw new InvalidOperationException(
                $"PHASE 3 ASSERTION FAILED: Stream Instrument property '{Instrument}' does not match CanonicalInstrument '{CanonicalInstrument}'. " +
                $"Instrument property must represent logic identity (canonical)."
            );
        }
        
        // PHASE 3: Assert Stream ID is canonical (does not contain execution instrument)
        if (ExecutionInstrument != CanonicalInstrument && Stream.IndexOf(ExecutionInstrument, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new InvalidOperationException(
                $"PHASE 3 ASSERTION FAILED: Execution instrument '{ExecutionInstrument}' found in Stream ID '{Stream}'. " +
                $"Stream ID must use canonical instrument '{CanonicalInstrument}'."
            );
        }
        
        // Build canonical intent (uses canonical Instrument for logic identity)
        var intent = new Intent(
            TradingDate,
            Stream,
            Instrument,  // PHASE 3: Intent ID uses canonical instrument (logic identity)
            Session,
            SlotTimeChicago,
            direction,
            entryPrice,
            _intendedStopPrice,
            _intendedTargetPrice,
            _intendedBeTrigger,
            entryTimeUtc,
            triggerReason);

        var intentId = intent.ComputeIntentId();
        
        // Emit expectation declaration
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, ExecutionInstrument, 
            "INTENT_EXECUTION_EXPECTATION_DECLARED", new
        {
            intent_id = intentId,
            canonical_instrument = CanonicalInstrument,
            execution_instrument = ExecutionInstrument,
            policy_base_size = _orderQuantity,
            policy_max_size = _maxQuantity,
            source = "EXECUTION_POLICY_FILE"
        }));
        
        // Register policy expectation with adapter
        if (_executionAdapter is NinjaTraderSimAdapter simAdapterForPolicy)
        {
            simAdapterForPolicy.RegisterIntentPolicy(intentId, _orderQuantity, _maxQuantity, 
                CanonicalInstrument, ExecutionInstrument, "EXECUTION_POLICY_FILE");
        }

        // Check idempotency: Has this intent already been submitted?
        if (_executionJournal != null && _executionJournal.IsIntentSubmitted(intentId, TradingDate, Stream))
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, Instrument, "EXECUTION_SKIPPED_DUPLICATE",
                new
                {
                    reason = "INTENT_ALREADY_SUBMITTED",
                    trading_date = TradingDate,
                    stream = Stream,
                    direction,
                    entry_price = entryPrice
                }));
            return;
        }

        // Risk gate check
        if (_riskGate == null)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, Instrument, "EXECUTION_BLOCKED",
                new { reason = "RISK_GATE_NOT_INITIALIZED" }));
            return;
        }

        var streamArmed = !_journal.Committed && State != StreamState.DONE;
        var (allowed, reason, failedGates) = _riskGate.CheckGates(
            _executionMode,
            TradingDate,
            Stream,
            Instrument,
            Session,
            SlotTimeChicago,
            timetableValidated: true, // Timetable is validated at engine level
            streamArmed: streamArmed,
            utcNow);

        if (!allowed)
        {
            _riskGate.LogBlocked(intentId, Instrument, Stream, Session, SlotTimeChicago, TradingDate, 
                reason ?? "UNKNOWN", failedGates, streamArmed, true, utcNow);
            return;
        }

        // All checks passed - log execution allowed (permanent state-transition log)
        // PHASE 3: Include both canonical and execution identities in event payload
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "EXECUTION_ALLOWED", State.ToString(),
            new
            {
                intent_id = intentId,
                direction = direction,
                entry_price = entryPrice,
                trigger_reason = triggerReason,
                execution_instrument = ExecutionInstrument,  // PHASE 3: Include execution identity
                canonical_instrument = CanonicalInstrument,   // PHASE 3: Include canonical identity
                note = "All gates passed, submitting entry order"
            }));

        // All checks passed - submit entry order
        if (_executionAdapter == null)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, Instrument, "EXECUTION_BLOCKED",
                new { reason = "EXECUTION_ADAPTER_NOT_INITIALIZED" }));
            return;
        }

        // Submit entry order (quantity = code-controlled, Chart Trader ignored)
        // Use StopMarket for breakout entries, Limit for immediate entries
        var entryOrderType = triggerReason == "BREAKOUT" ? "STOP_MARKET" : "LIMIT";
        // PHASE 3: Assert ExecutionInstrument is set before order placement
        if (string.IsNullOrWhiteSpace(ExecutionInstrument))
        {
            throw new InvalidOperationException(
                "PHASE 3 ASSERTION FAILED: ExecutionInstrument is null or empty. Cannot place orders without execution instrument."
            );
        }
        
        // PHASE 3: Assert ExecutionInstrument differs from canonical (for micro futures)
        // This ensures we're not accidentally using canonical instrument for orders
        if (ExecutionInstrument == CanonicalInstrument && ExecutionInstrument != "ES" && ExecutionInstrument != "NQ" && ExecutionInstrument != "YM" && ExecutionInstrument != "CL" && ExecutionInstrument != "NG" && ExecutionInstrument != "GC" && ExecutionInstrument != "RTY")
        {
            // Allow same for regular futures (ES, NQ, etc.), but warn if unexpected
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "EXECUTION_INSTRUMENT_SAME_AS_CANONICAL", State.ToString(),
                new
                {
                    execution_instrument = ExecutionInstrument,
                    canonical_instrument = CanonicalInstrument,
                    note = "Execution and canonical instruments are the same (expected for regular futures)"
                }));
        }
        
        // CRITICAL: Register intent BEFORE order submission so protective orders can be placed on fill
        // This MUST happen before SubmitEntryOrder() is called, not after
        // Register intent with adapter for fill callback handling
        // Use type check and cast instead of reflection (RegisterIntent is internal)
        if (_executionAdapter is NinjaTraderSimAdapter simAdapterForIntent)
        {
            simAdapterForIntent.RegisterIntent(intent);
        }
        else
        {
            // CRITICAL ERROR: Execution adapter is not NinjaTraderSimAdapter - intent cannot be registered
            // This will cause protective orders to fail on fill
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, Instrument, "EXECUTION_ERROR",
                new
                {
                    error = "Execution adapter is not NinjaTraderSimAdapter - RegisterIntent() cannot be called",
                    execution_adapter_type = _executionAdapter?.GetType().Name ?? "NULL",
                    intent_id = intentId,
                    note = "CRITICAL: Protective orders will NOT be placed on fill because intent is not registered"
                }));
        }
        
        // PHASE 2: Use ExecutionInstrument for order placement (not canonical Instrument)
        // PHASE 3.2: Use code-controlled order quantity (Chart Trader quantity ignored)
        var entryResult = _executionAdapter.SubmitEntryOrder(
            intentId,
            ExecutionInstrument,
            direction,
            entryPrice,
            _orderQuantity, // PHASE 3.2: Code-controlled quantity, not Chart Trader
            entryOrderType,
            utcNow);

        // Record submission in journal
        if (_executionJournal != null)
        {
            if (entryResult.Success)
            {
                // PHASE 2: Journal uses ExecutionInstrument for execution tracking
                _executionJournal.RecordSubmission(intentId, TradingDate, Stream, ExecutionInstrument, "ENTRY", entryResult.BrokerOrderId, utcNow, entryPrice);
            }
            else
            {
                _executionJournal.RecordRejection(intentId, TradingDate, Stream, entryResult.ErrorMessage ?? "UNKNOWN_ERROR", utcNow);
            }
        }
        
        // If entry submitted successfully, log success
        // Protective orders will be submitted automatically on entry fill (STEP 4)
        if (entryResult.Success)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, Instrument, "ENTRY_SUBMITTED",
                new
                {
                    broker_order_id = entryResult.BrokerOrderId,
                    direction,
                    entry_price = entryPrice,
                    stop_price = _intendedStopPrice,
                    target_price = _intendedTargetPrice,
                    intent_registered = _executionAdapter is NinjaTraderSimAdapter,
                    note = "Protective orders will be submitted after entry fill confirmation"
                }));
        }
    }

    private void LogNoTradeMarketClose(DateTimeOffset utcNow)
    {
        _entryDetected = true; // Mark as processed
        _intendedDirection = null;
        _triggerReason = "NO_TRADE_MARKET_CLOSE";

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "DRYRUN_INTENDED_ENTRY", State.ToString(),
            new
            {
                intended_trade = false,
                direction = (string?)null,
                entry_time_utc = (string?)null,
                entry_time_chicago = (string?)null,
                entry_price = (decimal?)null,
                trigger_reason = "NO_TRADE_MARKET_CLOSE"
            }));
    }

    private void ComputeAndLogProtectiveOrders(DateTimeOffset utcNow)
    {
        if (_intendedDirection == null || !_intendedEntryPrice.HasValue)
            return;

        var direction = _intendedDirection;
        var entryPrice = _intendedEntryPrice.Value;
        
        // CRITICAL FIX: BE trigger can be computed without range (only needs entry price and base target)
        // Always compute BE trigger even if range isn't available
        var beTriggerPts = _baseTarget * 0.65m; // 65% of target
        var beTriggerPrice = direction == "Long" ? entryPrice + beTriggerPts : entryPrice - beTriggerPts;
        var beStopPrice = direction == "Long" ? entryPrice - _tickSize : entryPrice + _tickSize;
        
        // Store BE trigger immediately (required for break-even detection)
        _intendedBeTrigger = beTriggerPrice;

        // Stop and target require range - only compute if range is available
        if (!RangeHigh.HasValue || !RangeLow.HasValue)
        {
            // Range not available - log warning but still set BE trigger (critical for break-even detection)
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "PROTECTIVE_ORDERS_PARTIAL_COMPUTE", State.ToString(),
                new
                {
                    direction = direction,
                    entry_price = entryPrice,
                    be_trigger_price = beTriggerPrice,
                    be_stop_price = beStopPrice,
                    range_high_available = RangeHigh.HasValue,
                    range_low_available = RangeLow.HasValue,
                    warning = "Range not available - BE trigger computed but stop/target prices not set",
                    note = "BE trigger is set for break-even detection. Stop and target will be computed when range is available or from lock snapshot."
                }));
            return; // Exit early - stop/target can't be computed without range
        }

        var rangeSize = RangeHigh.Value - RangeLow.Value;

        // Compute target
        var targetPrice = direction == "Long" ? entryPrice + _baseTarget : entryPrice - _baseTarget;

        // Compute stop loss: min(range_size, 3 * target_pts)
        var maxSlPoints = 3 * _baseTarget;
        var slPoints = Math.Min(rangeSize, maxSlPoints);
        var stopPrice = direction == "Long" ? entryPrice - slPoints : entryPrice + slPoints;

        // Store computed values for execution
        _intendedStopPrice = stopPrice;
        _intendedTargetPrice = targetPrice;

        // Log protective orders (always log for DRYRUN parity)
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "DRYRUN_INTENDED_PROTECTIVE", State.ToString(),
            new
            {
                target_pts = _baseTarget,
                target_price = targetPrice,
                sl_points = slPoints,
                stop_price = stopPrice
            }));

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "DRYRUN_INTENDED_BE", State.ToString(),
            new
            {
                be_trigger_pts = beTriggerPts,
                be_trigger_price = beTriggerPrice,
                be_stop_price = beStopPrice,
                be_triggered = false, // Will be set if triggered during price tracking (future step)
                be_trigger_time_utc = (string?)null
            }));
    }

    /// <summary>
    /// Pure, lock-snapshot-driven protective computation (no reliance on mutable interim fields).
    /// Uses the same math as the normal entry path:
    /// - target = entry ± target_pts
    /// - stop distance = min(range_size, 3 * target_pts)
    /// - BE trigger = 65% of target distance
    /// </summary>
    private (decimal stopPrice, decimal targetPrice, decimal beTriggerPrice) ComputeProtectivesFromLockSnapshot(
        string direction,
        decimal entryPrice,
        decimal rangeHigh,
        decimal rangeLow)
    {
        var rangeSize = rangeHigh - rangeLow;

        // Target
        var targetPrice = direction == "Long" ? entryPrice + _baseTarget : entryPrice - _baseTarget;

        // Stop loss: min(range_size, 3 * target_pts)
        var maxSlPoints = 3 * _baseTarget;
        var slPoints = Math.Min(rangeSize, maxSlPoints);
        var stopPrice = direction == "Long" ? entryPrice - slPoints : entryPrice + slPoints;

        // BE trigger: 65% of target distance
        var beTriggerPts = _baseTarget * 0.65m;
        var beTriggerPrice = direction == "Long" ? entryPrice + beTriggerPts : entryPrice - beTriggerPts;

        return (stopPrice, targetPrice, beTriggerPrice);
    }

    /// <summary>
    /// Recompute all time boundaries (Chicago and UTC) for a given trading date.
    /// This is the single source of truth for time boundary computation.
    /// </summary>
    private void RecomputeTimeBoundaries(DateOnly tradingDate)
    {
        RecomputeTimeBoundaries(tradingDate, _spec, _time);
    }
    
    /// <summary>
    /// Recompute all time boundaries (Chicago and UTC) for a given trading date.
    /// Overload for use during construction when instance fields aren't available yet.
    /// </summary>
    private void RecomputeTimeBoundaries(DateOnly tradingDate, ParitySpec spec, TimeService time)
    {
        // PHASE 1: Construct Chicago times directly (authoritative)
        // CRITICAL: Chicago time is the source of truth - UTC is derived from it
        // CRITICAL: Always construct windows using exchange trading hours (DST-aware)
        var rangeStartChicago = spec.sessions[Session].range_start_time;
        RangeStartChicagoTime = time.ConstructChicagoTime(tradingDate, rangeStartChicago);
        SlotTimeChicagoTime = time.ConstructChicagoTime(tradingDate, SlotTimeChicago);
        var marketCloseChicagoTime = time.ConstructChicagoTime(tradingDate, spec.entry_cutoff.market_close_time);
        
        // DIAGNOSTIC: Log RangeStartChicagoTime initialization
        // This proves initialization happens and tells you when and from what it is derived
        // Note: During construction, _log may not be initialized, so we check
        if (_log != null)
        {
            var utcNow = DateTimeOffset.UtcNow;
            _log.Write(RobotEvents.Base(_time, utcNow, tradingDate.ToString("yyyy-MM-dd"), Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_START_INITIALIZED", State.ToString(),
                new
                {
                    stream_id = Stream,
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    range_start_time_string = rangeStartChicago,
                    trading_date = tradingDate.ToString("yyyy-MM-dd"),
                    source = "RecomputeTimeBoundaries",
                    note = "Diagnostic: RangeStartChicagoTime initialized from spec"
                }));
        }
        
        // PHASE 2: Derive UTC times from Chicago times (derived representation)
        RangeStartUtc = time.ConvertChicagoToUtc(RangeStartChicagoTime);
        SlotTimeUtc = time.ConvertChicagoToUtc(SlotTimeChicagoTime);
        MarketCloseUtc = time.ConvertChicagoToUtc(marketCloseChicagoTime);
        
        // CRITICAL: Check for DST transition on this trading date
        // Timezone edge case mitigation: Detect DST transitions that can cause missing/duplicate hours
        var startOffset = RangeStartChicagoTime.Offset;
        var endOffset = SlotTimeChicagoTime.Offset;
        var dstTransitionDetected = startOffset != endOffset;
        
        if (dstTransitionDetected)
        {
            // Log DST transition warning (only if we have logging available)
            // Note: During construction, _log may not be initialized, so we check
            if (_log != null)
            {
                _log.Write(RobotEvents.Base(time, DateTimeOffset.UtcNow, tradingDate.ToString("yyyy-MM-dd"), Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "DST_TRANSITION_DETECTED", "STREAM_INIT",
                    new
                    {
                        trading_date = tradingDate.ToString("yyyy-MM-dd"),
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                        start_offset = startOffset.ToString(),
                        end_offset = endOffset.ToString(),
                        note = "DST transition detected - session may have missing or duplicate hour. Expected bar count may differ from nominal."
                    }));
            }
        }
    }
    
    /// <summary>
    /// Reset all daily state flags for a new trading day.
    /// Called during trading date rollover to clear accumulated state.
    /// </summary>
    private void ResetDailyState()
    {
        // Range tracking
        RangeHigh = null;
        RangeLow = null;
        FreezeClose = null;
        FreezeCloseSource = "UNSET";
        
        // Entry tracking
        _lastCloseBeforeLock = null;
        _entryDetected = false;
        _intendedDirection = null;
        _intendedEntryPrice = null;
        _intendedEntryTimeUtc = null;
        _triggerReason = null;
        _rangeLocked = false;
        _rangeLockCommitted = false;
        _rangeLockAttemptedAtUtc = null;
        _rangeLockFailureCount = 0;
        _breakoutLevelsMissing = false;
        _preHydrationComplete = false;
        
        // Reset bar source tracking counters
        lock (_barBufferLock)
        {
            _historicalBarCount = 0;
            _liveBarCount = 0;
            _dedupedBarCount = 0;
            _barSourceMap.Clear(); // Clear source tracking map
        }
        _filteredFutureBarCount = 0;
        _filteredPartialBarCount = 0;
        
        // Assertion flags
        _rangeIntentAssertEmitted = false;
        _firstBarAcceptedAssertEmitted = false;
        _rangeLockAssertEmitted = false;
        
        // Pre-hydration and gap tracking
        // CRITICAL: Since we clear the bar buffer, we need to re-run pre-hydration
        _preHydrationComplete = false;
        _largestSingleGapMinutes = 0.0;
        _totalGapMinutes = 0.0;
        _lastBarOpenChicago = null;
        _rangeInvalidated = false;
        _rangeInvalidatedNotified = false;
        
        // Logging state
        _slotEndSummaryLogged = false;
        _lastHeartbeatUtc = null;
        _lastBarReceivedUtc = null;
        _lastBarTimestampUtc = null;
        _rangeComputeStartLogged = false;
        _lastRangeComputeFailedLogUtc = null;
    }

    /// <summary>
    /// Get the current bar buffer count (thread-safe).
    /// </summary>
    private int GetBarBufferCount()
    {
        lock (_barBufferLock)
        {
            return _barBuffer.Count;
        }
    }

    /// <summary>
    /// Get a snapshot of the bar buffer (thread-safe copy).
    /// </summary>
    private List<Bar> GetBarBufferSnapshot()
    {
        lock (_barBufferLock)
        {
            return new List<Bar>(_barBuffer);
        }
    }
    
    /// <summary>
    /// Calculate next slot time UTC (next occurrence of slot_time).
    /// Reference: Analyzer's get_next_slot_time() logic.
    /// Assumes standard trading calendar. Early closes/holidays may cause slot expiry timing drift.
    /// </summary>
    private DateTimeOffset CalculateNextSlotTimeUtc(DateOnly tradingDate, string slotTimeChicago, TimeService time)
    {
        var currentChicago = time.ConstructChicagoTime(tradingDate, slotTimeChicago);
        var currentDate = DateOnly.FromDateTime(currentChicago.Date);
        var currentDateAsDateTime = currentChicago.Date; // For DayOfWeek property
        
        // Determine next trading day (handle Friday→Monday skip)
        DateOnly nextDate;
        if (currentDateAsDateTime.DayOfWeek == DayOfWeek.Friday)
        {
            // Friday → Monday (skip weekend)
            nextDate = currentDate.AddDays(3);
        }
        else
        {
            // Regular day → next day
            nextDate = currentDate.AddDays(1);
        }
        
        // Construct next slot time in Chicago timezone
        var nextSlotTimeChicago = time.ConstructChicagoTime(nextDate, slotTimeChicago);
        
        // Convert to UTC
        var nextSlotTimeUtc = time.ConvertChicagoToUtc(nextSlotTimeChicago);
        
        return nextSlotTimeUtc;
    }
    
    /// <summary>
    /// Check if entry fill exists for given intent ID (helper for post-entry verification).
    /// </summary>
    private bool HasEntryFillForIntentId(string intentId, string? tradingDate = null)
    {
        if (_executionJournal == null || string.IsNullOrWhiteSpace(intentId)) return false;
        
        // Try to load ExecutionJournalEntry by scanning journal directory
        // If tradingDate is null, search across all dates (for cross-date scenarios)
        var pattern = tradingDate != null
            ? $"{tradingDate}_*_{intentId}.json"
            : $"*_*_{intentId}.json";
        var journalDir = Path.Combine(_projectRoot, "data", "execution_journals");
        
        try
        {
            if (!Directory.Exists(journalDir)) return false;
            
            var files = Directory.GetFiles(journalDir, pattern);
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null && (entry.EntryFilled || entry.EntryFilledQuantityTotal > 0))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Skip corrupted files
                }
            }
        }
        catch
        {
            // Fail-safe: return false on error
        }
        
        return false;
    }
    
    /// <summary>
    /// Handle forced flatten at market close (execution interruption, not slot completion).
    /// CRITICAL: Must NEVER call Commit() or set Committed=true or State=DONE.
    /// </summary>
    public void HandleForcedFlatten(DateTimeOffset utcNow)
    {
        if (_journal.Committed || _journal.SlotStatus != SlotStatus.ACTIVE)
        {
            return; // Already committed or not active
        }
        
        // Guard: Verify post-entry condition
        var isPostEntry = false;
        if (!string.IsNullOrWhiteSpace(_journal.OriginalIntentId))
        {
            isPostEntry = HasEntryFillForIntentId(_journal.OriginalIntentId, TradingDate);
        }
        else if (_executionJournal != null)
        {
            // Fallback: check if any entry fill exists for this stream
            isPostEntry = _executionJournal.HasEntryFillForStream(TradingDate, Stream);
        }
        
        if (!isPostEntry)
        {
            // Pre-entry forced flatten - this shouldn't happen, but if it does, mark as NO_TRADE
            var previousStatus = _journal.SlotStatus;
            _journal.SlotStatus = SlotStatus.NO_TRADE;
            if (previousStatus != SlotStatus.NO_TRADE)
            {
                LogHealth("INFO", "SLOT_STATUS_CHANGED", $"Slot status changed: {previousStatus} -> {SlotStatus.NO_TRADE}",
                    new
                    {
                        previous_status = previousStatus.ToString(),
                        new_status = SlotStatus.NO_TRADE.ToString(),
                        commit_reason = "NO_TRADE_FORCED_FLATTEN_PRE_ENTRY",
                        slot_instance_key = _journal.SlotInstanceKey
                    });
            }
            Commit(utcNow, "NO_TRADE_FORCED_FLATTEN_PRE_ENTRY", "FORCED_FLATTEN_MARKET_CLOSE");
            return;
        }
        
        // Post-entry forced flatten: Set interruption flag, do NOT commit
        _journal.ExecutionInterruptedByClose = true;
        _journal.ForcedFlattenTimestamp = utcNow;
        
        // Store OriginalIntentId if not already stored (reference only, not duplication)
        if (string.IsNullOrWhiteSpace(_journal.OriginalIntentId) && _executionJournal != null)
        {
            // Find the intent ID for this stream's entry fill
            var pattern = $"{TradingDate}_{Stream}_*.json";
            var journalDir = Path.Combine(_projectRoot, "data", "execution_journals");
            
            try
            {
                if (Directory.Exists(journalDir))
                {
                    var files = Directory.GetFiles(journalDir, pattern);
                    foreach (var file in files)
                    {
                        try
                        {
                            var json = File.ReadAllText(file);
                            var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                            if (entry != null && (entry.EntryFilled || entry.EntryFilledQuantityTotal > 0))
                            {
                                _journal.OriginalIntentId = entry.IntentId;
                                break;
                            }
                        }
                        catch
                        {
                            // Skip corrupted files
                        }
                    }
                }
            }
            catch
            {
                // Fail-safe: continue without OriginalIntentId
            }
        }
        
        // Persist journal (do NOT commit)
        _journals.Save(_journal);
        
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "FORCED_FLATTEN_MARKET_CLOSE", State.ToString(),
            new
            {
                execution_interrupted = true,
                forced_flatten_timestamp = utcNow.ToString("o"),
                original_intent_id = _journal.OriginalIntentId ?? "NULL",
                slot_status = _journal.SlotStatus.ToString(),
                note = "Forced flatten at market close - execution interrupted, slot remains ACTIVE for re-entry"
            }));
    }
    
    /// <summary>
    /// Handle slot expiry (next slot time reached).
    /// </summary>
    private void HandleSlotExpiry(DateTimeOffset utcNow)
    {
        if (_journal.SlotStatus != SlotStatus.ACTIVE)
        {
            return; // Already expired or terminal
        }
        
        // Exit position at market if open (via execution adapter)
        // Handle both original entry and re-entry positions
        if (_executionAdapter != null)
        {
            // Flatten original entry position if OriginalIntentId exists
            if (!string.IsNullOrWhiteSpace(_journal.OriginalIntentId))
            {
                try
                {
                    _executionAdapter.Flatten(_journal.OriginalIntentId, ExecutionInstrument, utcNow);
                }
                catch
                {
                    // Log error but continue with expiry
                }
            }
            
            // Flatten re-entry position if re-entry happened
            if (_journal.ReentryFilled && !string.IsNullOrWhiteSpace(_journal.ReentryIntentId))
            {
                try
                {
                    _executionAdapter.Flatten(_journal.ReentryIntentId, ExecutionInstrument, utcNow);
                }
                catch
                {
                    // Log error but continue with expiry
                }
            }
        }
        
        // Cancel orders for both intents
        if (_executionAdapter != null)
        {
            // Cancel original entry orders
            if (!string.IsNullOrWhiteSpace(_journal.OriginalIntentId))
            {
                try
                {
                    if (_executionAdapter is NinjaTraderSimAdapter simAdapter)
                    {
                        simAdapter.CancelIntentOrders(_journal.OriginalIntentId, utcNow);
                    }
                }
                catch
                {
                    // Log error but continue with expiry
                }
            }
            
            // Cancel re-entry orders if re-entry happened
            if (_journal.ReentryFilled && !string.IsNullOrWhiteSpace(_journal.ReentryIntentId))
            {
                try
                {
                    if (_executionAdapter is NinjaTraderSimAdapter simAdapter)
                    {
                        simAdapter.CancelIntentOrders(_journal.ReentryIntentId, utcNow);
                    }
                }
                catch
                {
                    // Log error but continue with expiry
                }
            }
        }
        
        // Set SlotStatus=EXPIRED and commit lifecycle terminal
        var previousStatus = _journal.SlotStatus;
        _journal.SlotStatus = SlotStatus.EXPIRED;
        if (previousStatus != SlotStatus.EXPIRED)
        {
            LogHealth("INFO", "SLOT_STATUS_CHANGED", $"Slot status changed: {previousStatus} -> {SlotStatus.EXPIRED}",
                new
                {
                    previous_status = previousStatus.ToString(),
                    new_status = SlotStatus.EXPIRED.ToString(),
                    commit_reason = "SLOT_EXPIRED",
                    slot_instance_key = _journal.SlotInstanceKey
                });
        }
        Commit(utcNow, "SLOT_EXPIRED", "SLOT_EXPIRED");
        
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "SLOT_EXPIRED", State.ToString(),
            new
            {
                next_slot_time_utc = _journal.NextSlotTimeUtc?.ToString("o") ?? "NULL",
                slot_status = _journal.SlotStatus.ToString(),
                note = "Slot expired at next slot time"
            }));
    }
    
    /// <summary>
    /// Check for market open re-entry (time-based, not bar-based).
    /// Evaluated in Tick() - trigger = now >= session_open AND market live (tick/price observed).
    /// </summary>
    private void CheckMarketOpenReentry(DateTimeOffset utcNow)
    {
        if (_journal.SlotStatus != SlotStatus.ACTIVE || 
            !_journal.ExecutionInterruptedByClose || 
            _journal.ReentrySubmitted ||
            string.IsNullOrWhiteSpace(_journal.OriginalIntentId) ||
            _executionJournal == null ||
            _executionAdapter == null)
        {
            return; // Conditions not met for re-entry
        }
        
        // Time gate: now >= session_open_time (use RangeStartChicagoTime as proxy for session open)
        var nowChicago = _time.ConvertUtcToChicago(utcNow);
        if (nowChicago < RangeStartChicagoTime)
        {
            return; // Before session open
        }
        
        // Market tradeable signal: At least one tick/price observed since reopen
        // For now, assume market is live if we're past session open (can be enhanced with actual tick observation)
        // TODO: Add explicit tick observation tracking for more precise market-open detection
        
        // Check slot not expired
        if (_journal.NextSlotTimeUtc.HasValue && utcNow >= _journal.NextSlotTimeUtc.Value)
        {
            return; // Slot expired, no re-entry
        }
        
        // Load bracket levels from ExecutionJournalEntry via OriginalIntentId (canonical source)
        // CRITICAL: OriginalIntentId was stored from previous trading date, so search across all dates
        // Use PriorJournalKey to get original TradingDate if available, otherwise search all dates
        string? originalTradingDate = null;
        if (!string.IsNullOrWhiteSpace(_journal.PriorJournalKey))
        {
            // PriorJournalKey format: "{PreviousTradingDate}_{Stream}"
            var parts = _journal.PriorJournalKey.Split('_');
            if (parts.Length >= 1)
            {
                originalTradingDate = parts[0];
            }
        }
        
        var pattern = originalTradingDate != null
            ? $"{originalTradingDate}_*_{_journal.OriginalIntentId}.json"
            : $"*_*_{_journal.OriginalIntentId}.json"; // Search all dates if PriorJournalKey not available
        var journalDir = Path.Combine(_projectRoot, "data", "execution_journals");
        ExecutionJournalEntry? originalEntry = null;
        
        try
        {
            if (Directory.Exists(journalDir))
            {
                var files = Directory.GetFiles(journalDir, pattern);
                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                        if (entry != null && entry.IntentId == _journal.OriginalIntentId)
                        {
                            originalEntry = entry;
                            break;
                        }
                    }
                    catch
                    {
                        // Skip corrupted files
                    }
                }
            }
        }
        catch
        {
            // Fail-safe: cannot load entry
        }
        
        if (originalEntry == null || !originalEntry.EntryFilled)
        {
            // Cannot load original entry or entry not filled - fail closed
            var previousStatus = _journal.SlotStatus;
            _journal.SlotStatus = SlotStatus.FAILED_RUNTIME;
            if (previousStatus != SlotStatus.FAILED_RUNTIME)
            {
                LogHealth("INFO", "SLOT_STATUS_CHANGED", $"Slot status changed: {previousStatus} -> {SlotStatus.FAILED_RUNTIME}",
                    new
                    {
                        previous_status = previousStatus.ToString(),
                        new_status = SlotStatus.FAILED_RUNTIME.ToString(),
                        commit_reason = "REENTRY_FAILED_CANNOT_LOAD_ORIGINAL_ENTRY",
                        slot_instance_key = _journal.SlotInstanceKey
                    });
            }
            Commit(utcNow, "REENTRY_FAILED_CANNOT_LOAD_ORIGINAL_ENTRY", "REENTRY_FAILED");
            LogHealth("CRITICAL", "REENTRY_FAILED", $"Re-entry failed: Cannot load original ExecutionJournalEntry for intent {_journal.OriginalIntentId}",
                new { original_intent_id = _journal.OriginalIntentId });
            return;
        }
        
        // Pre-re-entry safety check: Verify stop price validity and protection placement conditions
        // For now, assume safety checks pass (can be enhanced with actual price gap detection)
        // TODO: Add explicit gap detection and price validity checks
        
        // Generate deterministic ReentryIntentId (does NOT include TradingDate)
        if (string.IsNullOrWhiteSpace(_journal.ReentryIntentId))
        {
            _journal.ReentryIntentId = $"{_journal.SlotInstanceKey}_REENTRY";
        }
        
        // Submit MARKET entry using direction/quantity from ExecutionJournalEntry, with ReentryIntentId
        var direction = originalEntry.Direction;
        var quantity = originalEntry.EntryFilledQuantityTotal;
        
        if (quantity <= 0)
        {
            // Invalid quantity - fail closed
            var previousStatus = _journal.SlotStatus;
            _journal.SlotStatus = SlotStatus.FAILED_RUNTIME;
            if (previousStatus != SlotStatus.FAILED_RUNTIME)
            {
                LogHealth("INFO", "SLOT_STATUS_CHANGED", $"Slot status changed: {previousStatus} -> {SlotStatus.FAILED_RUNTIME}",
                    new
                    {
                        previous_status = previousStatus.ToString(),
                        new_status = SlotStatus.FAILED_RUNTIME.ToString(),
                        commit_reason = "REENTRY_FAILED_INVALID_QUANTITY",
                        slot_instance_key = _journal.SlotInstanceKey
                    });
            }
            Commit(utcNow, "REENTRY_FAILED_INVALID_QUANTITY", "REENTRY_FAILED");
            LogHealth("CRITICAL", "REENTRY_FAILED", $"Re-entry failed: Invalid quantity {quantity}",
                new { original_intent_id = _journal.OriginalIntentId, quantity });
            return;
        }
        
        // Mark re-entry as submitted (idempotency)
        _journal.ReentrySubmitted = true;
        _journals.Save(_journal);
        
        // Submit MARKET order (implementation depends on execution adapter)
        // For now, log the re-entry attempt - actual order submission will be handled by execution adapter
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "REENTRY_SUBMITTED", State.ToString(),
            new
            {
                reentry_intent_id = _journal.ReentryIntentId,
                original_intent_id = _journal.OriginalIntentId,
                direction = direction ?? "NULL",
                quantity = quantity,
                stop_price = originalEntry.StopPrice,
                target_price = originalEntry.TargetPrice,
                entry_price = originalEntry.EntryPrice,
                note = "Re-entry MARKET order submitted at market open"
            }));
        
        // Note: Actual order submission and fill handling will be done by execution adapter
        // On fill, execution adapter should call HandleReentryFill() which will submit protective bracket
    }
    
    /// <summary>
    /// Handle re-entry fill (called by execution adapter when re-entry order fills).
    /// </summary>
    public void HandleReentryFill(string reentryIntentId, DateTimeOffset utcNow)
    {
        if (_journal.ReentryIntentId != reentryIntentId || _journal.ReentryFilled)
        {
            return; // Not our re-entry or already filled
        }
        
        _journal.ReentryFilled = true;
        _journals.Save(_journal);
        
        // Load bracket levels from OriginalIntentId
        if (string.IsNullOrWhiteSpace(_journal.OriginalIntentId) || _executionJournal == null)
        {
            // Cannot load bracket levels - fail closed
            _journal.SlotStatus = SlotStatus.FAILED_RUNTIME;
            var previousStatus = _journal.SlotStatus;
            _journal.SlotStatus = SlotStatus.FAILED_RUNTIME;
            if (previousStatus != SlotStatus.FAILED_RUNTIME)
            {
                LogHealth("INFO", "SLOT_STATUS_CHANGED", $"Slot status changed: {previousStatus} -> {SlotStatus.FAILED_RUNTIME}",
                    new
                    {
                        previous_status = previousStatus.ToString(),
                        new_status = SlotStatus.FAILED_RUNTIME.ToString(),
                        commit_reason = "REENTRY_PROTECTION_FAILED_CANNOT_LOAD_BRACKET",
                        slot_instance_key = _journal.SlotInstanceKey
                    });
            }
            Commit(utcNow, "REENTRY_PROTECTION_FAILED_CANNOT_LOAD_BRACKET", "REENTRY_PROTECTION_FAILED");
            LogHealth("CRITICAL", "REENTRY_PROTECTION_FAILED", $"Re-entry protection failed: Cannot load bracket levels for intent {_journal.OriginalIntentId}",
                new { original_intent_id = _journal.OriginalIntentId });
            return;
        }
        
        // Submit protective bracket (implementation depends on execution adapter)
        // For now, mark protection as submitted - actual submission will be handled by execution adapter
        _journal.ProtectionSubmitted = true;
        _journals.Save(_journal);
        
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "REENTRY_FILLED", State.ToString(),
            new
            {
                reentry_intent_id = reentryIntentId,
                original_intent_id = _journal.OriginalIntentId,
                note = "Re-entry filled - protective bracket should be submitted"
            }));
        
        // Note: Actual protective order submission will be done by execution adapter
        // On acceptance, execution adapter should call HandleReentryProtectionAccepted()
    }
    
    /// <summary>
    /// Handle re-entry protection acceptance (called by execution adapter when protective orders accepted).
    /// </summary>
    public void HandleReentryProtectionAccepted(DateTimeOffset utcNow)
    {
        if (!_journal.ProtectionSubmitted)
        {
            return; // Protection not submitted
        }
        
        _journal.ProtectionAccepted = true;
        _journal.ExecutionInterruptedByClose = false; // Clear interruption flag after protection confirmed
        _journals.Save(_journal);
        
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "REENTRY_PROTECTION_ACCEPTED", State.ToString(),
            new
            {
                reentry_intent_id = _journal.ReentryIntentId,
                original_intent_id = _journal.OriginalIntentId,
                note = "Re-entry protection accepted - slot resumed normal operation"
            }));
    }

    /// <summary>
    /// Add a bar to the buffer (thread-safe) with centralized deduplication precedence.
    /// 
    /// PRECEDENCE LADDER (formalized):
    /// LIVE > BARSREQUEST > CSV
    /// 
    /// This ensures deterministic behavior when multiple sources provide the same bar:
    /// - Live bars always win (most current, vendor corrections)
    /// - BarsRequest bars win over CSV (more authoritative historical source)
    /// - CSV bars are lowest priority (fallback/supplement)
    /// 
    /// All deduplication logic is centralized here - call sites only need to specify source.
    /// </summary>
    /// <param name="bar">Bar to add</param>
    /// <param name="source">Bar source (LIVE, BARSREQUEST, or CSV)</param>
    private void AddBarToBuffer(Bar bar, BarSource source)
    {
        lock (_barBufferLock)
        {
            var utcNow = DateTimeOffset.UtcNow;
            
            // Log buffer add attempt
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "BAR_BUFFER_ADD_ATTEMPT", State.ToString(),
                new
                {
                    stream_id = Stream,
                    canonical_instrument = CanonicalInstrument,
                    instrument = Instrument,
                    bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                    bar_timestamp_chicago = _time.ConvertUtcToChicago(bar.TimestampUtc).ToString("o"),
                    bar_source = source.ToString(),
                    current_buffer_count = _barBuffer.Count,
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o")
                }));
            
            var barAgeMinutes = (utcNow - bar.TimestampUtc).TotalMinutes;
            const double MIN_BAR_AGE_MINUTES = 1.0; // Bar period (1 minute bars)
            
            // Liveness Fix: Bypass age-based rejection for LIVE bars (live feed)
            // BarSource.LIVE means "live feed", not "OnBarClose completeness"
            // Live feeds can deliver bars that appear "young" relative to engine clock
            // (reconnects, scheduler delays, timestamp conventions)
            // Closedness is enforced by NinjaTrader configuration (Calculate = OnBarClose),
            // not by BarSource.LIVE. We no longer enforce age completeness for LIVE;
            // closedness is enforced by NT configuration.
            if (source != BarSource.LIVE && barAgeMinutes < MIN_BAR_AGE_MINUTES)
            {
                // Bar is too recent - likely partial/in-progress, reject it
                // (Only for BARSREQUEST and CSV sources - historical bars may be partial)
                var nowChicago = _time.ConvertUtcToChicago(utcNow);
                var barChicagoTime = _time.ConvertUtcToChicago(bar.TimestampUtc);
                
                if (_enableDiagnosticLogs)
                {
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_PARTIAL_REJECTED_BUFFER", State.ToString(),
                        new
                        {
                            instrument = Instrument,
                            stream_id = Stream,
                            trading_date = TradingDate,
                            bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                            bar_timestamp_chicago = barChicagoTime.ToString("o"),
                            current_time_utc = utcNow.ToString("o"),
                            current_time_chicago = nowChicago.ToString("o"),
                            bar_age_minutes = barAgeMinutes,
                            min_bar_age_minutes = MIN_BAR_AGE_MINUTES,
                            buffer_count = _barBuffer.Count,
                            stream_state = State.ToString(),
                            rejection_reason = "PARTIAL_BAR",
                            bar_source = source.ToString(),
                            note = "Bar rejected at buffer insert - too recent, likely partial/in-progress bar. Only fully closed bars accepted."
                        }));
                }
                
                // HIGH-SIGNAL WARNING: Partial bar rejection during steady-state (ARMED/RANGE_BUILDING) indicates issue
                if (State == StreamState.ARMED || State == StreamState.RANGE_BUILDING)
                {
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_PARTIAL_REJECTED_STEADY_STATE", State.ToString(),
                        new
                        {
                            instrument = Instrument,
                            stream_id = Stream,
                            stream_state = State.ToString(),
                            bar_source = source.ToString(),
                            warning = "Partial bar rejected during steady-state - may indicate OnBarClose timing issue",
                            note = "OnBarClose bars should be fully closed. Check bar source and timing."
                        }));
                }
                
                // Log buffer rejection
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BAR_BUFFER_REJECTED", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        canonical_instrument = CanonicalInstrument,
                        instrument = Instrument,
                        bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                        bar_timestamp_chicago = barChicagoTime.ToString("o"),
                        bar_source = source.ToString(),
                        rejection_reason = "PARTIAL_BAR",
                        bar_age_minutes = barAgeMinutes,
                        min_bar_age_minutes = MIN_BAR_AGE_MINUTES,
                        current_buffer_count = _barBuffer.Count,
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o")
                    }));
                
                return; // Reject partial bar
            }
            
            // For LIVE bars with suspicious age, log warning but accept (liveness guarantee)
            if (source == BarSource.LIVE && barAgeMinutes < MIN_BAR_AGE_MINUTES)
            {
                var nowChicago = _time.ConvertUtcToChicago(utcNow);
                var barChicagoTime = _time.ConvertUtcToChicago(bar.TimestampUtc);
                
                // Rate-limit warning to once per stream per 5 minutes
                var shouldLogWarning = !_lastLiveBarAgeWarningUtc.HasValue || 
                    (utcNow - _lastLiveBarAgeWarningUtc.Value).TotalMinutes >= 5.0;
                
                if (shouldLogWarning)
                {
                    _lastLiveBarAgeWarningUtc = utcNow;
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_PARTIAL_WARNING_LIVE_FEED", State.ToString(),
                        new
                        {
                            instrument = Instrument,
                            stream_id = Stream,
                            trading_date = TradingDate,
                            bar_age_minutes = Math.Round(barAgeMinutes, 3),
                            bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                            bar_timestamp_chicago = barChicagoTime.ToString("o"),
                            current_time_utc = utcNow.ToString("o"),
                            current_time_chicago = nowChicago.ToString("o"),
                            note = "LIVE bar age < 1.0 minute - may indicate timebase issue. Bar accepted for liveness. Closedness enforced by NT OnBarClose configuration."
                        }));
                }
                // Continue to accept bar (liveness guarantee)
            }
            
            // CRITICAL: Deduplicate by (instrument, barStartUtc) with formalized precedence
            // This is REQUIRED in a hybrid system (BarsRequest + CSV + live bars).
            //
            // Boundary-minute ambiguity problem:
            // - Strategy starts at 07:25:00.200 CT
            // - BarsRequest includes 07:25 bar (barStartUtc = 07:25:00)
            // - CSV may also include 07:25 bar
            // - Live feed may emit the 07:25 bar again at 07:26:00 (when bar closes)
            // - Even with <= now filtering, this can happen
            //
            // Historical vs live OHLC mismatch problem:
            // - NinjaTrader historical bars and live bars may have different OHLC values
            // - May differ due to data vendor corrections
            // - Example: Historical high = 4952.25, Live high = 4952.50 for same minute
            // - What breaks: Range values differ depending on startup timing, non-reproducible results
            //
            // FORMALIZED PRECEDENCE LADDER: LIVE > BARSREQUEST > CSV
            // - Live bars always win (most current, vendor corrections)
            // - BarsRequest bars win over CSV (more authoritative historical source)
            // - CSV bars are lowest priority (fallback/supplement)
            //
            // Rule: (instrument, barStartUtc) must be unique in _barBuffer
            // Note: bar.TimestampUtc represents the bar's start time (e.g., 07:25:00 = bar from 07:25 to 07:26)
            
            // Check if a bar with this timestamp (barStartUtc) already exists
            var existingBarIndex = _barBuffer.FindIndex(b => b.TimestampUtc == bar.TimestampUtc);
            
            if (existingBarIndex >= 0)
            {
                // Bar with this barStartUtc already exists - check precedence
                var existingBar = _barBuffer[existingBarIndex];
                var existingSource = _barSourceMap.TryGetValue(bar.TimestampUtc, out var existingBarSource) ? existingBarSource : BarSource.CSV; // Default to CSV if not tracked
                
                // PRECEDENCE CHECK: Only replace if new source has higher precedence
                // LIVE (0) > BARSREQUEST (1) > CSV (2) - lower enum value = higher precedence
                if (source < existingSource)
                {
                    // New bar has higher precedence - replace existing bar
                    _barBuffer[existingBarIndex] = bar;
                    _barSourceMap[bar.TimestampUtc] = source;
                    
                    // Check if OHLC values differ (for logging)
                    var ohlcDiffers = existingBar.Open != bar.Open || 
                                     existingBar.High != bar.High || 
                                     existingBar.Low != bar.Low || 
                                     existingBar.Close != bar.Close;
                    
                    // Log replacement if OHLC differs or at diagnostic level
                    if (ohlcDiffers || _enableDiagnosticLogs)
                    {
                        _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "BAR_DUPLICATE_REPLACED", State.ToString(),
                            new
                            {
                                bar_start_utc = bar.TimestampUtc.ToString("o"),
                                existing_source = existingSource.ToString(),
                                new_source = source.ToString(),
                                existing_bar_ohlc = new { O = existingBar.Open, H = existingBar.High, L = existingBar.Low, C = existingBar.Close },
                                new_bar_ohlc = new { O = bar.Open, H = bar.High, L = bar.Low, C = bar.Close },
                                ohlc_differs = ohlcDiffers,
                                buffer_count = _barBuffer.Count,
                                precedence_rule = "LIVE > BARSREQUEST > CSV",
                                note = ohlcDiffers 
                                    ? $"Duplicate bar replaced - {source} bar replaced {existingSource} bar (OHLC values differ)"
                                    : $"Duplicate bar replaced - {source} bar replaced {existingSource} bar (boundary-minute ambiguity)"
                            }));
                    }
                    
                    // Track deduplication
                    _dedupedBarCount++;
                    
                    // Update counters: decrement old source, increment new source
                    // (Only if sources differ - if same source, no counter change needed)
                    if (existingSource != source)
                    {
                        DecrementBarSourceCounter(existingSource);
                        IncrementBarSourceCounter(source);
                    }
                    
                    // Log buffer rejection (replaced, not added)
                    _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_BUFFER_REJECTED", State.ToString(),
                        new
                        {
                            stream_id = Stream,
                            canonical_instrument = CanonicalInstrument,
                            instrument = Instrument,
                            bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                            bar_timestamp_chicago = _time.ConvertUtcToChicago(bar.TimestampUtc).ToString("o"),
                            bar_source = source.ToString(),
                            rejection_reason = "DUPLICATE_REPLACED",
                            existing_source = existingSource.ToString(),
                            new_source = source.ToString(),
                            current_buffer_count = _barBuffer.Count,
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                            note = "Bar replaced existing bar with lower precedence"
                        }));
                    
                    return; // Bar replaced, don't add again
                }
                else
                {
                    // Existing bar has higher or equal precedence - reject new bar
                    if (_enableDiagnosticLogs)
                    {
                        _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "BAR_DUPLICATE_REJECTED", State.ToString(),
                            new
                            {
                                bar_start_utc = bar.TimestampUtc.ToString("o"),
                                existing_source = existingSource.ToString(),
                                new_source = source.ToString(),
                                precedence_rule = "LIVE > BARSREQUEST > CSV",
                                note = $"Duplicate bar rejected - existing {existingSource} bar has higher or equal precedence than new {source} bar"
                            }));
                    }
                    
                    // Log buffer rejection
                    _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_BUFFER_REJECTED", State.ToString(),
                        new
                        {
                            stream_id = Stream,
                            canonical_instrument = CanonicalInstrument,
                            instrument = Instrument,
                            bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                            bar_timestamp_chicago = _time.ConvertUtcToChicago(bar.TimestampUtc).ToString("o"),
                            bar_source = source.ToString(),
                            rejection_reason = "DUPLICATE_LOWER_PRECEDENCE",
                            existing_source = existingSource.ToString(),
                            new_source = source.ToString(),
                            current_buffer_count = _barBuffer.Count,
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                            note = "Duplicate bar rejected - existing bar has higher or equal precedence"
                        }));
                    
                    return; // Reject bar - existing bar has higher precedence
                }
            }
            
            // No duplicate - add bar to buffer
            _barBuffer.Add(bar);
            _barSourceMap[bar.TimestampUtc] = source;
            
            // Track bar source counters (increment for new bar)
            IncrementBarSourceCounter(source);
            
            // Log successful buffer add
            _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "BAR_BUFFER_ADD_COMMITTED", State.ToString(),
                new
                {
                    stream_id = Stream,
                    canonical_instrument = CanonicalInstrument,
                    instrument = Instrument,
                    bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                    bar_timestamp_chicago = _time.ConvertUtcToChicago(bar.TimestampUtc).ToString("o"),
                    bar_source = source.ToString(),
                    new_buffer_count = _barBuffer.Count,
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o")
                }));
        }
    }
    
    /// <summary>
    /// Increment bar source counter based on source type.
    /// Helper method to maintain accurate counts for logging.
    /// </summary>
    private void IncrementBarSourceCounter(BarSource source)
    {
        switch (source)
        {
            case BarSource.LIVE:
                _liveBarCount++;
                break;
            case BarSource.BARSREQUEST:
            case BarSource.CSV:
                _historicalBarCount++;
                break;
        }
    }
    
    /// <summary>
    /// Decrement bar source counter based on source type.
    /// Used when replacing a bar with a higher-precedence source.
    /// </summary>
    private void DecrementBarSourceCounter(BarSource source)
    {
        switch (source)
        {
            case BarSource.LIVE:
                _liveBarCount--;
                break;
            case BarSource.BARSREQUEST:
            case BarSource.CSV:
                _historicalBarCount--;
                break;
        }
    }

    /// <summary>
    /// Create a standardized log data object for range information.
    /// </summary>
    private object CreateRangeLogData(decimal? rangeHigh, decimal? rangeLow, decimal? freezeClose, string freezeCloseSource)
    {
        return new
        {
            range_high = rangeHigh,
            range_low = rangeLow,
            range_size = rangeHigh.HasValue && rangeLow.HasValue ? (decimal?)(rangeHigh.Value - rangeLow.Value) : (decimal?)null,
            freeze_close = freezeClose,
            freeze_close_source = freezeCloseSource
        };
    }

    /// <summary>
    /// Check if execution mode is SIM.
    /// Used to determine if BarsRequest pre-hydration is available (SIM mode uses NinjaTrader BarsRequest).
    /// </summary>
    private bool IsSimMode() => _executionMode == ExecutionMode.SIM;

    /// <summary>
    /// Check if stream is stuck in current state and log warning if so.
    /// </summary>
    private void CheckForStuckState(DateTimeOffset utcNow, string stateName, object? context = null)
    {
        // Rate-limit stuck state checks (once per 5 minutes max)
        if (_lastStuckStateCheckUtc.HasValue && 
            (utcNow - _lastStuckStateCheckUtc.Value).TotalMinutes < 5.0)
        {
            return; // Too soon to check again
        }
        
        _lastStuckStateCheckUtc = utcNow;
        
        if (!_stateEntryTimeUtc.HasValue)
        {
            _stateEntryTimeUtc = utcNow; // Initialize if not set
            return;
        }
        
        var timeInState = (utcNow - _stateEntryTimeUtc.Value).TotalMinutes;
        
        // Define expected maximum time in each state (in minutes)
        var maxTimeInState = stateName switch
        {
            "PRE_HYDRATION" => 30.0, // Should complete quickly
            "ARMED" => 60.0, // Can wait up to range_start_time
            "RANGE_BUILDING" => 120.0, // Can take up to slot_time
            "RANGE_LOCKED" => 480.0, // Can last until market close
            _ => 60.0 // Default threshold
        };
        
        if (timeInState > maxTimeInState)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STREAM_STATE_STUCK", State.ToString(),
                new
                {
                    state = stateName,
                    time_in_state_minutes = Math.Round(timeInState, 2),
                    max_expected_time_minutes = maxTimeInState,
                    state_entry_time_utc = _stateEntryTimeUtc.Value.ToString("o"),
                    current_time_utc = utcNow.ToString("o"),
                    context = context,
                    note = $"Stream has been in {stateName} state for {Math.Round(timeInState, 1)} minutes, exceeding expected maximum of {maxTimeInState} minutes"
                }));
        }
    }
    
    /// <summary>
    /// PHASE 3: Transition to new state and log with both canonical and execution identities.
    /// </summary>
    private void Transition(DateTimeOffset utcNow, StreamState next, string eventType, object? extra = null)
    {
        var previousState = State;
        var timeInPreviousState = _stateEntryTimeUtc.HasValue 
            ? (utcNow - _stateEntryTimeUtc.Value).TotalMinutes 
            : (double?)null;
        
        var nowChicago = _time.ConvertUtcToChicago(utcNow);
        var barCount = GetBarBufferCount();
        
        // Inverted check: If transitioning to RANGE_LOCKED, verify _rangeLocked == true
        if (next == StreamState.RANGE_LOCKED && !_rangeLocked)
        {
            LogHealth("ERROR", "RANGE_LOCK_TRANSITION_INVALID", 
                "Transitioning to RANGE_LOCKED state without _rangeLocked flag being set",
                new
                {
                    violation = "TRANSITION_WITHOUT_LOCK",
                    range_locked = _rangeLocked,
                    next_state = next.ToString(),
                    note = "State transition to RANGE_LOCKED must only occur after TryLockRange sets _rangeLocked = true"
                });
        }
        
        State = next;
        var stateEntryTimeUtc = utcNow; // Track when we entered this state
        _stateEntryTimeUtc = stateEntryTimeUtc;
        _journal.LastState = next.ToString();
        _journal.LastUpdateUtc = utcNow.ToString("o");
        _journals.Save(_journal);
        
        // Inverted check: If _rangeLocked == true and state is not RANGE_LOCKED after slot time, log CRITICAL
        // This detects the partial failure scenario where lock was committed but transition failed
        if (_rangeLocked && State != StreamState.RANGE_LOCKED && utcNow >= SlotTimeUtc)
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
        
        // Log hydration events for state transitions (edge events only)
        try
        {
            var chicagoNow = _time.ConvertUtcToChicago(utcNow);
            
            // ARMED transition
            if (next == StreamState.ARMED && previousState == StreamState.PRE_HYDRATION)
            {
                var armedData = new Dictionary<string, object>
                {
                    ["previous_state"] = previousState.ToString(),
                    ["transition_reason"] = eventType,
                    ["time_in_previous_state_minutes"] = timeInPreviousState.HasValue ? Math.Round(timeInPreviousState.Value, 2) : (double?)null,
                    ["bar_count"] = barCount
                };
                
                var hydrationEvent = new HydrationEvent(
                    eventType: "ARMED",
                    tradingDay: TradingDate,
                    streamId: Stream,
                    canonicalInstrument: CanonicalInstrument,
                    executionInstrument: ExecutionInstrument,
                    session: Session,
                    slotTimeChicago: SlotTimeChicago,
                    timestampUtc: utcNow,
                    timestampChicago: chicagoNow,
                    state: next.ToString(),
                    data: armedData
                );
                
                _hydrationPersister?.Persist(hydrationEvent);
            }
            // RANGE_BUILDING_START transition
            else if (next == StreamState.RANGE_BUILDING && previousState == StreamState.ARMED)
            {
                var rangeBuildingData = new Dictionary<string, object>
                {
                    ["previous_state"] = previousState.ToString(),
                    ["transition_reason"] = eventType,
                    ["time_in_previous_state_minutes"] = timeInPreviousState.HasValue ? Math.Round(timeInPreviousState.Value, 2) : (double?)null,
                    ["range_start_chicago"] = RangeStartChicagoTime.ToString("o"),
                    ["slot_time_chicago"] = SlotTimeChicagoTime.ToString("o"),
                    ["bar_count"] = barCount
                };
                
                var hydrationEvent = new HydrationEvent(
                    eventType: "RANGE_BUILDING_START",
                    tradingDay: TradingDate,
                    streamId: Stream,
                    canonicalInstrument: CanonicalInstrument,
                    executionInstrument: ExecutionInstrument,
                    session: Session,
                    slotTimeChicago: SlotTimeChicago,
                    timestampUtc: utcNow,
                    timestampChicago: chicagoNow,
                    state: next.ToString(),
                    data: rangeBuildingData
                );
                
                _hydrationPersister?.Persist(hydrationEvent);
            }
        }
        catch (Exception)
        {
            // Fail-safe: hydration logging never throws
        }
        
        // MANDATORY: Emit STREAM_STATE_TRANSITION event for watchdog observability (plan requirement #2)
        // PHASE 3: Include both canonical and execution identities in event payload
        // This event includes only state movement fields, not derived state like range_high/low
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, 
            "STREAM_STATE_TRANSITION", next.ToString(), 
            new
            {
                previous_state = previousState.ToString(),
                new_state = next.ToString(),
                state_entry_time_utc = stateEntryTimeUtc.ToString("o"),
                time_in_previous_state_minutes = timeInPreviousState.HasValue ? Math.Round(timeInPreviousState.Value, 2) : (double?)null,
                execution_instrument = ExecutionInstrument,  // PHASE 3: Execution identity (root name, e.g., "M2K")
                execution_instrument_full_name = ExecutionInstrument,  // Full contract name - use ExecutionInstrument for now (full name not available in StreamStateMachine)
                canonical_instrument = CanonicalInstrument   // PHASE 3: Canonical identity
            }));
        
        // Log state transition with full context (original event for backward compatibility)
        // PHASE 3: Include both identities in full context log
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, eventType, next.ToString(), 
            new
            {
                instrument = Instrument,  // Canonical (top-level field for backward compatibility)
                execution_instrument = ExecutionInstrument,  // PHASE 3: Execution identity
                canonical_instrument = CanonicalInstrument,   // PHASE 3: Canonical identity
                stream_id = Stream,
                trading_date = TradingDate,
                previous_state = previousState.ToString(),
                new_state = next.ToString(),
                time_in_previous_state_minutes = timeInPreviousState.HasValue ? Math.Round(timeInPreviousState.Value, 2) : (double?)null,
                now_chicago = nowChicago.ToString("o"),
                range_start_chicago = RangeStartChicagoTime.ToString("o"),
                bar_count = barCount,
                transition_event = eventType,
                extra_data = extra
            }));
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, "JOURNAL_WRITTEN", next.ToString(),
            new { committed = _journal.Committed, commit_reason = _journal.CommitReason }));
        
        // RANGE_INTENT_ASSERT: Emit once per stream per day when transitioning to ARMED
        if (next == StreamState.ARMED && !_rangeIntentAssertEmitted)
        {
            _rangeIntentAssertEmitted = true;
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_INTENT_ASSERT", next.ToString(),
                new
                {
                    trading_date = TradingDate,
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                    range_start_utc = RangeStartUtc.ToString("o"),
                    slot_time_utc = SlotTimeUtc.ToString("o"),
                    chicago_offset = RangeStartChicagoTime.Offset.ToString(),
                    source = "pre-slot assertion"
                }));
        }
    }
}

