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
    DONE
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
    private StreamJournal _journal;
    private readonly decimal _tickSize;
    private readonly string _timetableHash;
    private readonly ExecutionMode _executionMode;
    private readonly decimal _baseTarget;
    private readonly IExecutionAdapter? _executionAdapter;
    private readonly RiskGate? _riskGate;
    private readonly ExecutionJournal? _executionJournal;
    
    // PHASE 4: Alert callback for missing data incidents
    private Action<string, string, string, int>? _alertCallback;

    // Diagnostic logging configuration
    private readonly bool _enableDiagnosticLogs;
    private readonly int _barDiagnosticRateLimitSeconds;
    private readonly int _slotGateDiagnosticRateLimitSeconds;

    // Bar buffer for retrospective range computation (live mode)
    private readonly List<Bar> _barBuffer = new();
    private readonly object _barBufferLock = new();
    private bool _rangeComputed = false; // Flag to prevent multiple range computations
    
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
    
    // Pre-hydration state
    private bool _preHydrationComplete = false; // Must be true before entering ARMED/RANGE_BUILDING
    
    // Gap tolerance tracking (all times in Chicago, bar OPEN times)
    private double _largestSingleGapMinutes = 0.0; // Largest single gap seen
    private double _totalGapMinutes = 0.0; // Cumulative gap time
    private DateTimeOffset? _lastBarOpenChicago = null; // Last bar open time (Chicago) for gap calculation
    private bool _rangeInvalidated = false; // Permanently invalidated due to gap violation
    private bool _rangeInvalidatedNotified = false; // Track if RANGE_INVALIDATED notification already sent for this slot
    
    // Gap tolerance constants
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
    private bool _rangeComputeStartLogged = false; // Ensure RANGE_COMPUTE_START only logged once per slot
    private DateTimeOffset? _lastRangeComputeFailedLogUtc = null; // Rate-limit RANGE_COMPUTE_FAILED (once per minute max)
    private DateTimeOffset? _lastBarFilteringLogUtc = null; // Rate-limit RANGE_COMPUTE_BAR_FILTERING (once per minute max)
    private DateTimeOffset? _stateEntryTimeUtc = null; // Track when current state was entered (for stuck detection)
    private DateTimeOffset? _lastStuckStateCheckUtc = null; // Rate-limit stuck state checks (once per 5 minutes max)
    
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
        ExecutionMode executionMode = ExecutionMode.DRYRUN,
        IExecutionAdapter? executionAdapter = null,
        RiskGate? riskGate = null,
        ExecutionJournal? executionJournal = null,
        LoggingConfig? loggingConfig = null // Optional: logging configuration for diagnostic control
    )
    {
        _time = time;
        _spec = spec;
        _log = log;
        _journals = journals;
        _timetableHash = timetableHash;
        _executionMode = executionMode;
        _executionAdapter = executionAdapter;
        _riskGate = riskGate;
        _executionJournal = executionJournal;

        // Load diagnostic logging configuration
        _enableDiagnosticLogs = loggingConfig?.enable_diagnostic_logs ?? false;
        _barDiagnosticRateLimitSeconds = loggingConfig?.diagnostic_rate_limits?.bar_diagnostic_seconds ?? (_enableDiagnosticLogs ? 30 : 300);
        _slotGateDiagnosticRateLimitSeconds = loggingConfig?.diagnostic_rate_limits?.slot_gate_diagnostic_seconds ?? (_enableDiagnosticLogs ? 30 : 60);

        Stream = directive.stream;
        Instrument = directive.instrument.ToUpperInvariant();
        Session = directive.session;
        SlotTimeChicago = directive.slot_time;

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
                        policy = "RESTART_FULL_RECONSTRUCTION",
                        note = "Mid-session restart detected - will reconstruct range from historical + live bars. Result may differ from uninterrupted operation."
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
            TimetableHashAtCommit = null
        };
        
        // Initialize state entry time tracking
        _stateEntryTimeUtc = DateTimeOffset.UtcNow;
    }

    public bool IsSameInstrument(string instrument) =>
        string.Equals(Instrument, instrument, StringComparison.OrdinalIgnoreCase);
    
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

    public void ApplyDirectiveUpdate(string newSlotTimeChicago, DateOnly tradingDate, DateTimeOffset utcNow)
    {
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

        // Replace journal with one for the new trading_date
        if (existingJournal != null)
        {
            // Use existing journal for new date
            _journal = existingJournal;
        }
        else
        {
            // Create new journal entry for new trading_date
            _journal = new StreamJournal
            {
                TradingDate = newTradingDateStr,
                Stream = Stream,
                Committed = false,
                CommitReason = null,
                LastState = State.ToString(),
                LastUpdateUtc = utcNow.ToString("o"),
                TimetableHashAtCommit = null
            };
        }
        _journals.Save(_journal);

        // Only reset state and clear buffers if this is an actual forward rollover (not initialization or backward date)
        if (!isInitialization && !isBackwardDate)
        {
            // Reset all daily state for new trading day
            ResetDailyState();
            
            // A) Strategy lifecycle: New trading day detected
            LogHealth("INFO", "TRADING_DAY_ROLLOVER", $"New trading day detected: {newTradingDateStr}",
                new { previous_trading_date = previousTradingDateStr, new_trading_date = newTradingDateStr });
            
            // Clear bar buffer on reset
            lock (_barBufferLock)
            {
                _barBuffer.Clear();
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
        if (_journal.Committed)
        {
            // Hard no re-arming
            State = StreamState.DONE;
            return;
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
        }
    }

    /// <summary>
    /// Handle PRE_HYDRATION state logic.
    /// </summary>
    private void HandlePreHydrationState(DateTimeOffset utcNow)
    {
        // DRYRUN mode: Use file-based pre-hydration only
        // SIM mode: Use NinjaTrader BarsRequest only (no CSV files)
        if (!_preHydrationComplete)
        {
            if (IsSimMode())
            {
                // SIM mode: Skip CSV files, rely solely on BarsRequest
                // BarsRequest bars arrive via LoadPreHydrationBars() and are buffered in OnBar()
                // Mark pre-hydration as complete so we can transition when bars arrive
                _preHydrationComplete = true;
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
            if (IsSimMode())
            {
                // SIM mode: Wait for BarsRequest bars from NinjaTrader
                // Check if we have sufficient bars or if we're past range start time
                int barCount = GetBarBufferCount();
                var nowChicago = _time.ConvertUtcToChicago(utcNow);
                
                // Transition to ARMED if we have bars or if we're past range start time
                if (barCount > 0 || nowChicago >= RangeStartChicagoTime)
                {
                    // Log timeout if transitioning without bars
                    if (barCount == 0 && nowChicago >= RangeStartChicagoTime)
                    {
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
                    
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "HYDRATION_SUMMARY", "PRE_HYDRATION",
                        new
                        {
                            instrument = Instrument,
                            slot = Stream,
                            trading_date = TradingDate,
                            total_bars_in_buffer = barCount,
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
                            // Mode and source info
                            execution_mode = _executionMode.ToString(),
                            note = "Consolidated hydration summary - forensic snapshot at PRE_HYDRATION → ARMED transition. " +
                                   "This log captures all bar sources, filtering, and deduplication statistics for debugging and auditability."
                        }));
                    
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
                    Transition(utcNow, StreamState.ARMED, "PRE_HYDRATION_COMPLETE_SIM");
                }
                // Otherwise, wait for more bars from NinjaTrader (they'll be buffered in OnBar)
            }
            else
            {
                // DRYRUN mode: File-based pre-hydration complete, transition to ARMED
                // CONSOLIDATED HYDRATION SUMMARY LOG: Forensic snapshot for every day
                int barCount = GetBarBufferCount();
                int historicalBarCount, liveBarCount, dedupedBarCount, filteredFutureBarCount, filteredPartialBarCount;
                lock (_barBufferLock)
                {
                    historicalBarCount = _historicalBarCount;
                    liveBarCount = _liveBarCount;
                    dedupedBarCount = _dedupedBarCount;
                    filteredFutureBarCount = _filteredFutureBarCount;
                    filteredPartialBarCount = _filteredPartialBarCount;
                }
                
                var nowChicago = _time.ConvertUtcToChicago(utcNow);
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "HYDRATION_SUMMARY", "PRE_HYDRATION",
                        new
                        {
                            instrument = Instrument,
                            slot = Stream,
                            trading_date = TradingDate,
                            total_bars_in_buffer = barCount,
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
                            // Mode and source info
                            execution_mode = _executionMode.ToString(),
                            note = "Consolidated hydration summary - forensic snapshot at PRE_HYDRATION → ARMED transition. " +
                                   "This log captures all bar sources, filtering, and deduplication statistics for debugging and auditability."
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
                            transition_reason = "PRE_HYDRATION_COMPLETE"
                        }));
                
                Transition(utcNow, StreamState.ARMED, "PRE_HYDRATION_COMPLETE");
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
                    
                    // Compute initial range from available bars when entering RANGE_BUILDING
                    // Range will be updated incrementally from live bars until slot_time
                    
                    // Compute initial range from available bars (range_start → now)
                    // Range will be updated incrementally from live bars until slot_time
                    if (!_rangeComputed && utcNow < SlotTimeUtc)
                    {
                        // Compute range up to current time (not slot_time yet)
                        // Use RangeStartUtc to current time for initial computation
                        var initialRangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: utcNow);
                        
                        if (initialRangeResult.Success)
                        {
                            // Set initial range values (will be updated by live bars until slot_time)
                            RangeHigh = initialRangeResult.RangeHigh;
                            RangeLow = initialRangeResult.RangeLow;
                            FreezeClose = initialRangeResult.FreezeClose;
                            FreezeCloseSource = initialRangeResult.FreezeCloseSource;
                            _rangeComputed = true; // Mark as computed to prevent recomputation at slot_time
                            
                            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                                "RANGE_INITIALIZED_FROM_HISTORY", State.ToString(),
                                new
                                {
                                    range_high = RangeHigh,
                                    range_low = RangeLow,
                                    freeze_close = FreezeClose,
                                    freeze_close_source = FreezeCloseSource,
                                    bar_count = initialRangeResult.BarCount,
                                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                                    computed_up_to_chicago = _time.ConvertUtcToChicago(utcNow).ToString("o"),
                                    slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                                    note = "Range initialized from history, will be updated incrementally by live bars until slot_time"
                                }));
                        }
                        else
                        {
                            // Log failure to compute initial range
                            LogHealth("WARN", "RANGE_INITIALIZATION_FAILED", 
                                $"Failed to compute initial range from history. Will retry on next bar or tick.",
                                new
                                {
                                    reason = initialRangeResult.Reason ?? "UNKNOWN",
                                    bar_buffer_count = initialRangeResult.BarCount,
                                    range_start_utc = RangeStartUtc.ToString("o"),
                                    utc_now = utcNow.ToString("o")
                                });
                        }
                    }
                    else if (utcNow >= SlotTimeUtc && !_rangeComputed)
                    {
                        // CRITICAL: We're past slot time but range was never computed
                        // This is a failure case - log error and attempt recovery
                        LogHealth("ERROR", "RANGE_COMPUTE_MISSED_SLOT_TIME", 
                            $"Slot time reached but range was never computed. Attempting late computation.",
                            new
                            {
                                slot_time_utc = SlotTimeUtc.ToString("o"),
                                utc_now = utcNow.ToString("o"),
                                minutes_past_slot = (utcNow - SlotTimeUtc).TotalMinutes
                            });
                        
                        // Attempt to compute range anyway (may have partial data)
                        var lateRangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: SlotTimeUtc);
                        if (lateRangeResult.Success)
                        {
                            RangeHigh = lateRangeResult.RangeHigh;
                            RangeLow = lateRangeResult.RangeLow;
                            FreezeClose = lateRangeResult.FreezeClose;
                            FreezeCloseSource = lateRangeResult.FreezeCloseSource;
                            _rangeComputed = true;
                            
                            LogHealth("INFO", "RANGE_COMPUTED_LATE", 
                                "Range computed successfully after slot time (late but recovered)",
                                new
                                {
                                    range_high = RangeHigh,
                                    range_low = RangeLow,
                                    bar_count = lateRangeResult.BarCount
                                });
                        }
                    }
                }
    }

    /// <summary>
    /// Handle RANGE_BUILDING state logic.
    /// </summary>
    private void HandleRangeBuildingState(DateTimeOffset utcNow)
    {
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
                    execution_mode = _executionMode.ToString()
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
        
        // DIAGNOSTIC: Log slot gate evaluation (only if diagnostic logs enabled, and only on state change or rate limit)
        if (_enableDiagnosticLogs)
        {
            var gateDecision = utcNow >= SlotTimeUtc && !_rangeComputed;
            var gateStateChanged = gateDecision != _lastSlotGateState;
            
            // Log if state changed or rate limit exceeded
            var shouldLog = gateStateChanged || 
                           !_lastSlotGateDiagnostic.HasValue || 
                           (utcNow - _lastSlotGateDiagnostic.Value).TotalSeconds >= _slotGateDiagnosticRateLimitSeconds;
            
            if (shouldLog)
            {
                _lastSlotGateDiagnostic = utcNow;
                _lastSlotGateState = gateDecision;
                var comparisonUsed = $"utcNow ({utcNow:o}) >= SlotTimeUtc ({SlotTimeUtc:o}) && !_rangeComputed ({!_rangeComputed})";
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
                        range_computed_flag = _rangeComputed,
                        bar_buffer_count = barBufferCount,
                        time_until_slot_seconds = gateDecision ? 0 : (SlotTimeUtc - utcNow).TotalSeconds
                    }));
            }
        }
        
        if (utcNow >= SlotTimeUtc)
        {
            // If range is invalidated due to gap violation, prevent trading
            if (_rangeInvalidated)
            {
                // G) "Nothing happened" explanation: Trade blocked due to gap violation
                LogSlotEndSummary(utcNow, "RANGE_INVALIDATED", false, false, "Range invalidated due to gap tolerance violation");
                Commit(utcNow, "RANGE_INVALIDATED", "Gap tolerance violation");
                return;
            }
            
            if (!_rangeComputed)
            {
                // Range not yet computed - compute retrospectively from all bars
                // DETERMINISTIC: Ensure bars exist before computing
                
                // REFACTORED: Compute range retrospectively from completed session data
                // Do NOT build incrementally - treat session window as closed dataset
                var computeStart = DateTimeOffset.UtcNow;
                
                // Log RANGE_COMPUTE_START only once per stream per slot (prevents spam)
                if (!_rangeComputeStartLogged)
                {
                    _rangeComputeStartLogged = true;
                    int barBufferCount = GetBarBufferCount();
                    
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "RANGE_COMPUTE_START", State.ToString(),
                        new
                        {
                            range_start_utc = RangeStartUtc.ToString("o"),
                            range_end_utc = SlotTimeUtc.ToString("o"),
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            range_end_chicago = SlotTimeChicagoTime.ToString("o"),
                            slot_time_chicago = SlotTimeChicago,
                            time_conversion_method = "ConvertUtcToChicago",
                            time_conversion_note = "Pure timezone converter (UTC -> Chicago), not 'now'",
                            expected_chicago_window = $"{RangeStartChicagoTime:HH:mm} to {SlotTimeChicagoTime:HH:mm}",
                            expected_utc_window = $"{RangeStartUtc:HH:mm} to {SlotTimeUtc:HH:mm}",
                            bar_buffer_count = barBufferCount,
                            note = "Range is calculated for Chicago time window (UTC times shown for reference)"
                        }));
                }

                var rangeResult = ComputeRangeRetrospectively(utcNow);
                
                if (!rangeResult.Success)
                {
                    // Range computation failed - rate-limit logging to once per minute max
                    var shouldLogFailure = !_lastRangeComputeFailedLogUtc.HasValue || 
                        (utcNow - _lastRangeComputeFailedLogUtc.Value).TotalMinutes >= 1.0;
                    
                    if (shouldLogFailure)
                    {
                        _lastRangeComputeFailedLogUtc = utcNow;
                        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "RANGE_COMPUTE_FAILED", State.ToString(),
                            new
                            {
                                range_start_utc = RangeStartUtc.ToString("o"),
                                range_end_utc = SlotTimeUtc.ToString("o"),
                                reason = rangeResult.Reason,
                                bar_count = rangeResult.BarCount,
                                message = "Range computation failed - will retry on next tick or use partial data",
                                note = "This error is rate-limited to once per minute to reduce log noise"
                            }));
                    }
                    
                    // Check if stream is stuck in RANGE_BUILDING state
                    CheckForStuckState(utcNow, "RANGE_BUILDING", new
                    {
                        reason = "Range computation failing repeatedly",
                        range_compute_failure_reason = rangeResult.Reason,
                        bar_count = rangeResult.BarCount,
                        time_since_range_start_minutes = (utcNow - RangeStartUtc).TotalMinutes,
                        time_until_slot_minutes = (SlotTimeUtc - utcNow).TotalMinutes,
                        prerequisite_check = "Range computation requires bars in window"
                    });
                    
                    // Don't commit NO_TRADE - allow retry or partial range computation
                    return;
                }
                
                // Reset failure log timestamp on successful computation
                _lastRangeComputeFailedLogUtc = null;

                // Range computed successfully - set values atomically
                RangeHigh = rangeResult.RangeHigh;
                RangeLow = rangeResult.RangeLow;
                FreezeClose = rangeResult.FreezeClose;
                FreezeCloseSource = rangeResult.FreezeCloseSource;
                _rangeComputed = true;

                var computeDuration = (DateTimeOffset.UtcNow - computeStart).TotalMilliseconds;

                // Calculate expected vs actual metrics for timezone edge case detection
                // CRITICAL: Bar count mismatch is NOT an error - it's informational only
                // Partial-minute boundary ambiguity can cause differences:
                // - Bars can close early under low liquidity
                // - Bars can be emitted slightly late
                // - Timestamps can shift around DST/session boundaries
                // - Deduplication may reduce count
                // Bar count != expected count is acceptable and only logged, never asserted
                var sessionDurationMinutes = (SlotTimeChicagoTime - RangeStartChicagoTime).TotalMinutes;
                var expectedBarCount = (int)Math.Round(sessionDurationMinutes); // 1-minute bars (nominal)
                var actualBarCount = rangeResult.BarCount;
                var barCountDiff = actualBarCount - expectedBarCount;
                var barCountMismatch = Math.Abs(barCountDiff) > 5; // Allow 5 bar tolerance for informational logging
                
                // CRITICAL: Log bar source statistics for debugging clarity
                // Without explicit logs, you won't remember:
                // - Which bars came from BarsRequest (historical)
                // - Which came from live feed
                // - Whether filtering occurred
                // - What deduplication happened
                // This costs nothing and saves hours of debugging
                int historicalBarCount, liveBarCount, dedupedBarCount;
                lock (_barBufferLock)
                {
                    historicalBarCount = _historicalBarCount;
                    liveBarCount = _liveBarCount;
                    dedupedBarCount = _dedupedBarCount;
                }
                
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "RANGE_COMPUTE_COMPLETE", State.ToString(),
                    new
                    {
                        range_high = RangeHigh,
                        range_low = RangeLow,
                        range_size = RangeHigh.HasValue && RangeLow.HasValue ? (decimal?)(RangeHigh.Value - RangeLow.Value) : (decimal?)null,
                        freeze_close = FreezeClose,
                        freeze_close_source = FreezeCloseSource,
                        bar_count = rangeResult.BarCount,
                        expected_bar_count = expectedBarCount,
                        bar_count_mismatch = barCountMismatch,
                        bar_count_diff = barCountDiff,
                        note_bar_count_mismatch = barCountMismatch 
                            ? "Bar count differs from expected (informational only - not an error). Possible causes: partial-minute boundaries, DST transitions, deduplication, low liquidity early closes."
                            : "Bar count matches expected (within tolerance)",
                        session_length_minutes = sessionDurationMinutes,
                        duration_ms = computeDuration,
                        // CRITICAL: Bar source tracking for debugging clarity
                        historical_bar_count = historicalBarCount,
                        live_bar_count = liveBarCount,
                        deduped_bar_count = dedupedBarCount,
                        filtered_future_bar_count = _filteredFutureBarCount,
                        filtered_partial_bar_count = _filteredPartialBarCount,
                        total_bars_received = historicalBarCount + liveBarCount + dedupedBarCount,
                        // CRITICAL: Log both UTC and Chicago times for auditability
                        first_bar_utc = rangeResult.FirstBarUtc?.ToString("o"),
                        last_bar_utc = rangeResult.LastBarUtc?.ToString("o"),
                        first_bar_chicago = rangeResult.FirstBarChicago?.ToString("o"),
                        last_bar_chicago = rangeResult.LastBarChicago?.ToString("o"),
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        range_end_chicago = SlotTimeChicagoTime.ToString("o"),
                        range_start_utc = RangeStartUtc.ToString("o"),
                        range_end_utc = SlotTimeUtc.ToString("o"),
                        range_start_offset = RangeStartChicagoTime.Offset.ToString(),
                        range_end_offset = SlotTimeChicagoTime.Offset.ToString(),
                        range_invalidated = _rangeInvalidated,
                        largest_single_gap_minutes = _largestSingleGapMinutes,
                        total_gap_minutes = _totalGapMinutes
                    }));
                
                // RANGE_LOCK_ASSERT: Emit once per stream per day when range is locked
                if (!_rangeLockAssertEmitted && rangeResult.FirstBarChicago.HasValue && rangeResult.LastBarChicago.HasValue)
                {
                    _rangeLockAssertEmitted = true;
                    var firstBarChicago = rangeResult.FirstBarChicago.Value;
                    var lastBarChicago = rangeResult.LastBarChicago.Value;
                    var firstBarCheck = firstBarChicago >= RangeStartChicagoTime;
                    var lastBarCheck = lastBarChicago < SlotTimeChicagoTime;
                    
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "RANGE_LOCK_ASSERT", State.ToString(),
                        new
                        {
                            trading_date = TradingDate,
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                            bars_used_count = rangeResult.BarCount,
                            first_bar_chicago = firstBarChicago.ToString("o"),
                            last_bar_chicago = lastBarChicago.ToString("o"),
                            invariant_checks = new
                            {
                                first_bar_ge_range_start = firstBarCheck,
                                last_bar_lt_slot_time = lastBarCheck
                            },
                            note = "range locked"
                        }));
                }
            }
            else
            {
                // Range already computed incrementally - log and use existing values
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "RANGE_LOCKED_INCREMENTAL", State.ToString(),
                    new
                    {
                        range_high = RangeHigh,
                        range_low = RangeLow,
                        range_size = RangeHigh.HasValue && RangeLow.HasValue ? (decimal?)(RangeHigh.Value - RangeLow.Value) : (decimal?)null,
                        freeze_close = FreezeClose,
                        freeze_close_source = FreezeCloseSource,
                        note = "Range computed incrementally from history and live bars, locking at slot_time"
                    }));
            }

            // F) State machine invariants: Check for slot_time passed without range lock or failure
            if (utcNow >= SlotTimeUtc.AddMinutes(1) && !_rangeComputed && !_rangeInvalidated)
            {
                LogHealth("ERROR", "INVARIANT_VIOLATION", "Slot_time passed without range lock or failure — this should never happen",
                    new
                    {
                        violation = "SLOT_TIME_PASSED_WITHOUT_RESOLUTION",
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                        current_time_chicago = _time.ConvertUtcToChicago(utcNow).ToString("o"),
                        range_computed = _rangeComputed,
                        range_invalidated = _rangeInvalidated
                    });
            }
            
            // F) State machine invariants: Check for duplicate range lock attempt
            if (State == StreamState.RANGE_LOCKED)
            {
                LogHealth("ERROR", "INVARIANT_VIOLATION", "Range lock attempted twice — this should never happen",
                    new
                    {
                        violation = "DUPLICATE_RANGE_LOCK",
                        current_state = State.ToString(),
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o")
                    });
            }
            
            // Transition to RANGE_LOCKED only if range is valid
            if (!_rangeInvalidated)
            {
                var rangeLogData = CreateRangeLogData(RangeHigh, RangeLow, FreezeClose, FreezeCloseSource);
                Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED", rangeLogData);
                
                // G) "Nothing happened" explanation: Range locked summary
                LogSlotEndSummary(utcNow, "RANGE_VALID", true, false, "Range locked, awaiting signal");
            }
            else
            {
                // Range invalidated - commit and prevent trading
                LogSlotEndSummary(utcNow, "RANGE_INVALIDATED", false, false, "Range invalidated due to gap tolerance violation");
                Commit(utcNow, "RANGE_INVALIDATED", "Gap tolerance violation");
            }

            // Log range lock snapshot (all modes - was DRYRUN-only, now unconditional for consistency)
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_LOCK_SNAPSHOT", State.ToString(),
                new
                {
                    range_high = RangeHigh,
                    range_low = RangeLow,
                    range_size = RangeHigh.HasValue && RangeLow.HasValue ? (decimal?)(RangeHigh.Value - RangeLow.Value) : (decimal?)null,
                    freeze_close = FreezeClose,
                    freeze_close_source = FreezeCloseSource,
                    slot_time_chicago = SlotTimeChicago,
                    slot_time_utc = SlotTimeUtc.ToString("o")
                }));

            ComputeBreakoutLevelsAndLog(utcNow);

            // Check for immediate entry at lock (all execution modes)
            if (FreezeClose.HasValue && RangeHigh.HasValue && RangeLow.HasValue)
            {
                CheckImmediateEntryAtLock(utcNow);
            }

            // Log intended brackets (all execution modes)
            if (utcNow < MarketCloseUtc)
                LogIntendedBracketsPlaced(utcNow);
        }
    }

    /// <summary>
    /// Handle RANGE_LOCKED state logic.
    /// </summary>
    private void HandleRangeLockedState(DateTimeOffset utcNow)
    {
        // Check for market close cutoff (all execution modes)
        if (!_entryDetected && utcNow >= MarketCloseUtc)
        {
            LogNoTradeMarketClose(utcNow);
            Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE");
        }
    }

    public void Arm(DateTimeOffset utcNow)
    {
        if (_journal.Committed)
        {
            State = StreamState.DONE;
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
        
        // REFACTORED: Buffer bars for retrospective computation starting from PRE_HYDRATION state
        // This captures bars from NinjaTrader as they arrive, using them for pre-hydration in SIM mode
        // Buffer bars when in PRE_HYDRATION, ARMED, or RANGE_BUILDING state
        if (State == StreamState.PRE_HYDRATION || State == StreamState.ARMED || State == StreamState.RANGE_BUILDING)
        {
            
            // DIAGNOSTIC: Log bar reception details (only if diagnostic logs enabled)
            bool shouldLogBar = false;
            if (_enableDiagnosticLogs)
            {
                shouldLogBar = !_lastBarDiagnosticTime.HasValue || 
                              (utcNow - _lastBarDiagnosticTime.Value).TotalSeconds >= _barDiagnosticRateLimitSeconds;
                
                if (shouldLogBar)
                {
                    _lastBarDiagnosticTime = utcNow;
                    var inRange = barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime;
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
            
            // Buffer bars that fall within [range_start, slot_time) using Chicago time comparison
            // Range window is defined in Chicago time to match trading session semantics
            if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime)
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
                
                // Add bar to buffer with actual open price
                // Determine bar source: LIVE if from live feed, BARSREQUEST if marked as historical from BarsRequest
                var barSource = isHistorical ? BarSource.BARSREQUEST : BarSource.LIVE;
                AddBarToBuffer(new Bar(barUtc, open, high, low, close, null), barSource);
                
                // Gap tolerance tracking (treat bar timestamp as bar OPEN time in Chicago)
                if (_lastBarOpenChicago.HasValue)
                {
                    var gapMinutes = (barChicagoTime - _lastBarOpenChicago.Value).TotalMinutes;
                    
                    // Only track gaps > 1 minute (normal 1-minute bars have ~1 minute gaps)
                    if (gapMinutes > 1.0)
                    {
                        // Update gap tracking
                        if (gapMinutes > _largestSingleGapMinutes)
                            _largestSingleGapMinutes = gapMinutes;
                        
                        _totalGapMinutes += gapMinutes;
                        
                        // Check gap tolerance rules (invalidate immediately if violated)
                        bool violated = false;
                        string violationReason = "";
                        
                        if (gapMinutes > MAX_SINGLE_GAP_MINUTES)
                        {
                            violated = true;
                            violationReason = $"Single gap {gapMinutes:F1} minutes exceeds MAX_SINGLE_GAP_MINUTES ({MAX_SINGLE_GAP_MINUTES})";
                        }
                        else if (_totalGapMinutes > MAX_TOTAL_GAP_MINUTES)
                        {
                            violated = true;
                            violationReason = $"Total gap {_totalGapMinutes:F1} minutes exceeds MAX_TOTAL_GAP_MINUTES ({MAX_TOTAL_GAP_MINUTES})";
                        }
                        
                        // Check last 10 minutes rule
                        var last10MinStart = SlotTimeChicagoTime.AddMinutes(-10);
                        if (barChicagoTime >= last10MinStart && gapMinutes > MAX_GAP_LAST_10_MINUTES)
                        {
                            violated = true;
                            violationReason = $"Gap {gapMinutes:F1} minutes in last 10 minutes exceeds MAX_GAP_LAST_10_MINUTES ({MAX_GAP_LAST_10_MINUTES})";
                        }
                        
                        if (violated)
                        {
                            var wasInvalidated = _rangeInvalidated;
                            _rangeInvalidated = true;
                            
                            var gapViolationData = new
                            {
                                instrument = Instrument,
                                slot = Stream,
                                violation_reason = violationReason,
                                gap_minutes = gapMinutes,
                                largest_single_gap_minutes = _largestSingleGapMinutes,
                                total_gap_minutes = _totalGapMinutes,
                                previous_bar_open_chicago = _lastBarOpenChicago.Value.ToString("o"),
                                current_bar_open_chicago = barChicagoTime.ToString("o"),
                                slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                                gap_location = $"Between {_lastBarOpenChicago.Value:HH:mm} and {barChicagoTime:HH:mm} Chicago time",
                                minutes_until_slot_time = (SlotTimeChicagoTime - barChicagoTime).TotalMinutes,
                                note = gapMinutes > MAX_SINGLE_GAP_MINUTES 
                                    ? $"Single gap of {gapMinutes:F1} minutes exceeds limit of {MAX_SINGLE_GAP_MINUTES} minutes"
                                    : _totalGapMinutes > MAX_TOTAL_GAP_MINUTES
                                    ? $"Total gaps of {_totalGapMinutes:F1} minutes exceed limit of {MAX_TOTAL_GAP_MINUTES} minutes"
                                    : $"Gap of {gapMinutes:F1} minutes in last 10 minutes exceeds limit of {MAX_GAP_LAST_10_MINUTES} minutes"
                            };
                            
                            // Log to health directory (detailed health tracking)
                            LogHealth("ERROR", "GAP_TOLERANCE_VIOLATION", $"Range invalidated due to gap violation: {violationReason}", gapViolationData);
                            
                            // Also log to main engine log for easier discovery
                            log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                                "GAP_TOLERANCE_VIOLATION", State.ToString(), gapViolationData));
                            
                            // Notify RANGE_INVALIDATED once per slot (transition false → true)
                            if (!wasInvalidated && !_rangeInvalidatedNotified)
                            {
                                _rangeInvalidatedNotified = true;
                                var notificationKey = $"RANGE_INVALIDATED:{Stream}";
                                var title = $"Range Invalidated: {Instrument} {Stream}";
                                var message = $"Range invalidated due to gap violation: {violationReason}. Trading blocked for this slot.";
                                _alertCallback?.Invoke(notificationKey, title, message, 1); // High priority
                            }
                        }
                        else if (gapMinutes > 1.0)
                        {
                            // Gap within tolerance - log as WARN
                            LogHealth("WARN", "GAP_TOLERATED", $"Gap {gapMinutes:F1} minutes tolerated (within limits)",
                                new
                                {
                                    instrument = Instrument,
                                    slot = Stream,
                                    gap_minutes = gapMinutes,
                                    largest_single_gap_minutes = _largestSingleGapMinutes,
                                    total_gap_minutes = _totalGapMinutes,
                                    previous_bar_open_chicago = _lastBarOpenChicago.Value.ToString("o"),
                                    current_bar_open_chicago = barChicagoTime.ToString("o")
                                });
                        }
                    }
                }
                
                // Update last bar open time (Chicago, bar OPEN time)
                _lastBarOpenChicago = barChicagoTime;
                
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
        }
        else if (State == StreamState.RANGE_LOCKED)
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
        // Estimate bar interval (typically 1 minute for NG)
        var estimatedBarIntervalMinutes = 1;
        var barInterval = TimeSpan.FromMinutes(estimatedBarIntervalMinutes);
        var slotTimePlusInterval = slotTimeUtcParsed.Add(barInterval);
        
        if (barUtc >= slotTimePlusInterval && !finalAllowed && stateOk && slotReached)
        {
            // This should not happen - execution should be allowed by now
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "EXECUTION_GATE_INVARIANT_VIOLATION", State.ToString(),
                new
                {
                    error = "EXECUTION_SHOULD_BE_ALLOWED_BUT_IS_NOT",
                    bar_timestamp_chicago = barChicagoTime,
                    slot_time_chicago = slotTimeChicagoStr,
                    slot_time_utc = slotTimeUtcParsed.ToString("o"),
                    bar_interval_minutes = estimatedBarIntervalMinutes,
                    realtime_ok = realtimeOk,
                    trading_day = tradingDay,
                    session_active = sessionActive,
                    slot_reached = slotReached,
                    timetable_enabled = timetableEnabled,
                    stream_armed = streamArmed,
                    can_detect_entries = canDetectEntries,
                    entry_detected = _entryDetected,
                    breakout_levels_computed = _brkLongRounded.HasValue && _brkShortRounded.HasValue,
                    execution_mode = _executionMode.ToString(),
                    message = "Slot time has passed but execution is not allowed. Check gate states above."
                }));
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
            new { brackets_intended = true, note = "Skeleton does not place orders." }));
    }
    
    /// <summary>
    /// Log health/anomaly event to logs/health/ directory.
    /// </summary>
    private void LogHealth(string level, string eventType, string message, object? data = null)
    {
        try
        {
            var journalPath = _journals.GetJournalPath(TradingDate, Stream);
            var projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(journalPath)));
            if (string.IsNullOrEmpty(projectRoot))
                return;
            
            var healthDir = Path.Combine(projectRoot, "logs", "health");
            Directory.CreateDirectory(healthDir);
            
            var logFile = Path.Combine(healthDir, $"{TradingDate}_{Instrument}_{Stream}.jsonl");
            
            var utcNow = DateTimeOffset.UtcNow;
            var chicagoNow = _time.ConvertUtcToChicago(utcNow);
            
            var logEntry = new Dictionary<string, object?>
            {
                ["ts_utc"] = utcNow.ToString("o"),
                ["ts_chicago"] = chicagoNow.ToString("o"),
                ["level"] = level,
                ["event_type"] = eventType,
                ["message"] = message,
                ["trading_date"] = TradingDate,
                ["instrument"] = Instrument,
                ["slot"] = Stream,
                ["session"] = Session,
                ["state"] = State.ToString(),
                ["execution_mode"] = _executionMode.ToString(),
                ["data"] = data
            };
            
            var json = JsonUtil.Serialize(logEntry);
            File.AppendAllText(logFile, json + Environment.NewLine);
        }
        catch
        {
            // Silently fail - health logging must not crash trading logic
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
            
            return (false, null, null, null, "UNSET", 0, "NO_BARS_IN_WINDOW", null, null, null, null);
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

    private void Commit(DateTimeOffset utcNow, string commitReason, string eventType)
    {
        if (_journal.Committed)
        {
            State = StreamState.DONE;
            return;
        }

        _journal.Committed = true;
        _journal.CommitReason = commitReason;
        _journal.LastState = StreamState.DONE.ToString();
        _journal.LastUpdateUtc = utcNow.ToString("o");
        _journal.TimetableHashAtCommit = _timetableHash;

        _journals.Save(_journal);
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "JOURNAL_WRITTEN", StreamState.DONE.ToString(),
            new { committed = true, commit_reason = commitReason, timetable_hash_at_commit = _timetableHash }));

        State = StreamState.DONE;
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            eventType, State.ToString(),
            new { committed = true, commit_reason = commitReason }));
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
            // Log error but don't fail execution
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "INCIDENT_PERSIST_ERROR", State.ToString(),
                new { error = ex.Message, exception_type = ex.GetType().Name }));
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
        if (_entryDetected) return; // Already detected

        _entryDetected = true;
        _intendedDirection = direction;
        _intendedEntryPrice = entryPrice;
        _intendedEntryTimeUtc = entryTimeUtc;
        _triggerReason = triggerReason;

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

        // Build canonical intent
        var intent = new Intent(
            TradingDate,
            Stream,
            Instrument,
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

        var (allowed, reason) = _riskGate.CheckGates(
            _executionMode,
            TradingDate,
            Stream,
            Instrument,
            Session,
            SlotTimeChicago,
            timetableValidated: true, // Timetable is validated at engine level
            streamArmed: !_journal.Committed && State != StreamState.DONE,
            utcNow);

        if (!allowed)
        {
            _riskGate.LogBlocked(intentId, Instrument, reason ?? "UNKNOWN", utcNow);
            return;
        }

        // All checks passed - log execution allowed (permanent state-transition log)
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "EXECUTION_ALLOWED", State.ToString(),
            new
            {
                intent_id = intentId,
                direction = direction,
                entry_price = entryPrice,
                trigger_reason = triggerReason,
                note = "All gates passed, submitting entry order"
            }));

        // All checks passed - submit entry order
        if (_executionAdapter == null)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, Instrument, "EXECUTION_BLOCKED",
                new { reason = "EXECUTION_ADAPTER_NOT_INITIALIZED" }));
            return;
        }

        // Submit entry order (quantity = 1 contract per stream)
        var entryResult = _executionAdapter.SubmitEntryOrder(
            intentId,
            Instrument,
            direction,
            entryPrice, // Use limit order at entry price
            1, // Quantity: 1 contract
            utcNow);

        // Record submission in journal
        if (_executionJournal != null)
        {
            if (entryResult.Success)
            {
                _executionJournal.RecordSubmission(intentId, TradingDate, Stream, Instrument, "ENTRY", entryResult.BrokerOrderId, utcNow);
            }
            else
            {
                _executionJournal.RecordRejection(intentId, TradingDate, Stream, entryResult.ErrorMessage ?? "UNKNOWN_ERROR", utcNow);
            }
        }

        // If entry submitted successfully, register intent for fill callback handling
        // Protective orders will be submitted automatically on entry fill (STEP 4)
        if (entryResult.Success)
        {
            // Register intent with adapter for fill callback handling
            // Use type check and cast instead of reflection (RegisterIntent is internal)
            if (_executionAdapter is NinjaTraderSimAdapter ntAdapter)
            {
                ntAdapter.RegisterIntent(intent);
            }
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, Instrument, "ENTRY_SUBMITTED",
                new
                {
                    broker_order_id = entryResult.BrokerOrderId,
                    direction,
                    entry_price = entryPrice,
                    stop_price = _intendedStopPrice,
                    target_price = _intendedTargetPrice,
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
        if (_intendedDirection == null || !_intendedEntryPrice.HasValue || !RangeHigh.HasValue || !RangeLow.HasValue)
            return;

        var direction = _intendedDirection;
        var entryPrice = _intendedEntryPrice.Value;
        var rangeSize = RangeHigh.Value - RangeLow.Value;

        // Compute target
        var targetPrice = direction == "Long" ? entryPrice + _baseTarget : entryPrice - _baseTarget;

        // Compute stop loss: min(range_size, 3 * target_pts)
        var maxSlPoints = 3 * _baseTarget;
        var slPoints = Math.Min(rangeSize, maxSlPoints);
        var stopPrice = direction == "Long" ? entryPrice - slPoints : entryPrice + slPoints;

        // Compute BE trigger
        var beTriggerPts = _baseTarget * 0.65m; // 65% of target
        var beTriggerPrice = direction == "Long" ? entryPrice + beTriggerPts : entryPrice - beTriggerPts;
        var beStopPrice = direction == "Long" ? entryPrice - _tickSize : entryPrice + _tickSize;

        // Store computed values for execution
        _intendedStopPrice = stopPrice;
        _intendedTargetPrice = targetPrice;
        _intendedBeTrigger = beTriggerPrice;

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
        _rangeComputed = false;
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
            // CRITICAL: Reject partial bars - only accept fully closed bars
            // Partial-bar contamination problem:
            // - BarsRequest returns fully closed bars (good, but we verify)
            // - Live bars should be closed (OnBarClose), but defensive check needed
            // - If you start mid-minute, BarsRequest gives completed prior bar
            // - Live feed may later emit a bar that partially overlaps expectations
            // - What breaks: Off-by-one minute range errors, incomplete data
            //
            // Rule: Only accept bars that are at least 1 minute old (bar period)
            // This ensures the bar is fully closed before we use it
            var utcNow = DateTimeOffset.UtcNow;
            var barAgeMinutes = (utcNow - bar.TimestampUtc).TotalMinutes;
            const double MIN_BAR_AGE_MINUTES = 1.0; // Bar period (1 minute bars)
            
            if (barAgeMinutes < MIN_BAR_AGE_MINUTES)
            {
                // Bar is too recent - likely partial/in-progress, reject it
                if (_enableDiagnosticLogs)
                {
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_PARTIAL_REJECTED_BUFFER", State.ToString(),
                        new
                        {
                            bar_timestamp_utc = bar.TimestampUtc.ToString("o"),
                            current_time_utc = utcNow.ToString("o"),
                            bar_age_minutes = barAgeMinutes,
                            min_bar_age_minutes = MIN_BAR_AGE_MINUTES,
                            buffer_count = _barBuffer.Count,
                            note = "Bar rejected at buffer insert - too recent, likely partial/in-progress bar. Only fully closed bars accepted."
                        }));
                }
                return; // Reject partial bar
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
                    return; // Reject bar - existing bar has higher precedence
                }
            }
            
            // No duplicate - add bar to buffer
            _barBuffer.Add(bar);
            _barSourceMap[bar.TimestampUtc] = source;
            
            // Track bar source counters (increment for new bar)
            IncrementBarSourceCounter(source);
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
    
    private void Transition(DateTimeOffset utcNow, StreamState next, string eventType, object? extra = null)
    {
        var previousState = State;
        var timeInPreviousState = _stateEntryTimeUtc.HasValue 
            ? (utcNow - _stateEntryTimeUtc.Value).TotalMinutes 
            : (double?)null;
        
        State = next;
        _stateEntryTimeUtc = utcNow; // Track when we entered this state
        _journal.LastState = next.ToString();
        _journal.LastUpdateUtc = utcNow.ToString("o");
        _journals.Save(_journal);
        
        // Log state transition with context
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, eventType, next.ToString(), 
            new
            {
                previous_state = previousState.ToString(),
                new_state = next.ToString(),
                time_in_previous_state_minutes = timeInPreviousState.HasValue ? Math.Round(timeInPreviousState.Value, 2) : (double?)null,
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

