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

public sealed class StreamStateMachine
{
    
    public string Stream { get; }
    public string Instrument { get; }
    public string Session { get; }
    public string SlotTimeChicago { get; private set; }

    public StreamState State { get; private set; } = StreamState.PRE_HYDRATION;
    public bool Committed => _journal.Committed;
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

        // PHASE 1: Construct Chicago times directly (authoritative)
        // CRITICAL: Chicago time is the source of truth - UTC is derived from it
        var rangeStartChicago = spec.sessions[Session].range_start_time;
        RangeStartChicagoTime = time.ConstructChicagoTime(dateOnly, rangeStartChicago);
        SlotTimeChicagoTime = time.ConstructChicagoTime(dateOnly, SlotTimeChicago);
        var marketCloseChicagoTime = time.ConstructChicagoTime(dateOnly, spec.entry_cutoff.market_close_time);
        
        // PHASE 2: Derive UTC times from Chicago times (derived representation)
        RangeStartUtc = RangeStartChicagoTime.ToUniversalTime();
        SlotTimeUtc = SlotTimeChicagoTime.ToUniversalTime();
        MarketCloseUtc = marketCloseChicagoTime.ToUniversalTime();

        if (!spec.TryGetInstrument(Instrument, out var inst))
            throw new InvalidOperationException($"Instrument not found in parity spec: {Instrument}");
        _tickSize = inst.tick_size;
        _baseTarget = inst.base_target;

        var existing = journals.TryLoad(tradingDateStr, Stream);
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
    }

    public bool IsSameInstrument(string instrument) =>
        string.Equals(Instrument, instrument, StringComparison.OrdinalIgnoreCase);
    
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
        SlotTimeUtc = SlotTimeChicagoTime.ToUniversalTime();
        
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, "UPDATE_APPLIED", State.ToString(),
            new { slot_time_chicago = SlotTimeChicago, slot_time_chicago_time = SlotTimeChicagoTime.ToString("o"), slot_time_utc = SlotTimeUtc.ToString("o") }));
    }

    public void UpdateTradingDate(DateOnly newTradingDate, DateTimeOffset utcNow)
    {
        // Update trading_date and recompute all UTC times based on new date
        // This is called when replay crosses into a new trading day
        var newTradingDateStr = newTradingDate.ToString("yyyy-MM-dd");
        var previousTradingDateStr = _journal.TradingDate;

        // Only update if trading_date actually changed
        if (previousTradingDateStr == newTradingDateStr)
            return;

        // Load or create journal for the new trading_date
        var existingJournal = _journals.TryLoad(newTradingDateStr, Stream);
        if (existingJournal != null && existingJournal.Committed)
        {
            // Journal already exists and is committed - use it and mark as DONE
            _journal = existingJournal;
            State = StreamState.DONE;
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "TRADING_DAY_ROLLOVER", State.ToString(),
                new
                {
                    previous_trading_date = previousTradingDateStr,
                    new_trading_date = newTradingDateStr,
                    note = "JOURNAL_ALREADY_COMMITTED_FOR_NEW_DATE"
                }));
            return;
        }

        // PHASE 1: Reconstruct Chicago times directly for the new trading_date (authoritative)
        var rangeStartChicago = _spec.sessions[Session].range_start_time;
        RangeStartChicagoTime = _time.ConstructChicagoTime(newTradingDate, rangeStartChicago);
        SlotTimeChicagoTime = _time.ConstructChicagoTime(newTradingDate, SlotTimeChicago);
        var marketCloseChicagoTime = _time.ConstructChicagoTime(newTradingDate, _spec.entry_cutoff.market_close_time);
        
        // PHASE 2: Derive UTC times from Chicago times (derived representation)
        RangeStartUtc = RangeStartChicagoTime.ToUniversalTime();
        SlotTimeUtc = SlotTimeChicagoTime.ToUniversalTime();
        MarketCloseUtc = marketCloseChicagoTime.ToUniversalTime();

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

        // Reset daily state flags for new trading day
        RangeHigh = null;
        RangeLow = null;
        FreezeClose = null;
        FreezeCloseSource = "UNSET";
        _lastCloseBeforeLock = null;
        _entryDetected = false;
        _intendedDirection = null;
        _intendedEntryPrice = null;
        _intendedEntryTimeUtc = null;
        _triggerReason = null;
        _rangeComputed = false;
        
        // Reset assertion flags for new trading day
        _rangeIntentAssertEmitted = false;
        _firstBarAcceptedAssertEmitted = false;
        _rangeLockAssertEmitted = false;
        
        // Reset pre-hydration and gap tracking state for new trading day
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

        // Reset state to ARMED if we were in a mid-day state (preserve committed state)
        if (!_journal.Committed && State != StreamState.ARMED)
        {
            State = StreamState.ARMED;
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "TRADING_DAY_ROLLOVER", State.ToString(),
                new
                {
                    previous_trading_date = previousTradingDateStr,
                    new_trading_date = newTradingDateStr,
                    state_reset_to = "ARMED"
                }));
        }
        else
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "TRADING_DAY_ROLLOVER", State.ToString(),
                new
                {
                    previous_trading_date = previousTradingDateStr,
                    new_trading_date = newTradingDateStr,
                    state_preserved = State.ToString()
                }));
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
                if (!_preHydrationComplete)
                {
                    PerformPreHydration(utcNow);
                }
                
                // After pre-hydration completes, transition to ARMED
                if (_preHydrationComplete)
                {
                    Transition(utcNow, StreamState.ARMED, "PRE_HYDRATION_COMPLETE");
                }
                break;
                
            case StreamState.ARMED:
                // Require pre-hydration completion before entering RANGE_BUILDING
                if (!_preHydrationComplete)
                {
                    // Should not happen - pre-hydration should complete before ARMED
                    LogHealth("ERROR", "INVARIANT_VIOLATION", "ARMED state reached without pre-hydration completion",
                        new { instrument = Instrument, slot = Stream });
                    break;
                }
                
                if (utcNow >= RangeStartUtc)
                {
                    // A) Strategy lifecycle: New range window started
                    LogHealth("INFO", "RANGE_WINDOW_STARTED", $"Range window started for slot {SlotTimeChicago}",
                        new { range_start_chicago = RangeStartChicagoTime.ToString("o"), slot_time_chicago = SlotTimeChicagoTime.ToString("o") });
                    
                    Transition(utcNow, StreamState.RANGE_BUILDING, "RANGE_BUILD_START");
                    
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
                    }
                }
                break;

            case StreamState.RANGE_BUILDING:
                // B) Heartbeat / watchdog (throttled)
                if (!_lastHeartbeatUtc.HasValue || (utcNow - _lastHeartbeatUtc.Value).TotalMinutes >= HEARTBEAT_INTERVAL_MINUTES)
                {
                    _lastHeartbeatUtc = utcNow;
                    int liveBarCount;
                    lock (_barBufferLock)
                    {
                        liveBarCount = _barBuffer.Count;
                    }
                    
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
                        
                        lock (_barBufferLock)
                        {
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
                                    bar_buffer_count = _barBuffer.Count,
                                    time_until_slot_seconds = gateDecision ? 0 : (SlotTimeUtc - utcNow).TotalSeconds
                                }));
                        }
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
                        break;
                    }
                    
                    if (!_rangeComputed)
                    {
                        // Range not yet computed - compute retrospectively from all bars
                        // DETERMINISTIC: Ensure bars exist before computing
                        
                        // REFACTORED: Compute range retrospectively from completed session data
                        // Do NOT build incrementally - treat session window as closed dataset
                        var computeStart = DateTimeOffset.UtcNow;
                        
                        // Log time conversion method info once per stream
                        int barBufferCount;
                        lock (_barBufferLock)
                        {
                            barBufferCount = _barBuffer.Count;
                        }
                        
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

                        var rangeResult = ComputeRangeRetrospectively(utcNow);
                        
                        if (!rangeResult.Success)
                        {
                            // Range computation failed - log but continue (allow retry or partial ranges)
                            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                                "RANGE_COMPUTE_FAILED", State.ToString(),
                                new
                                {
                                    range_start_utc = RangeStartUtc.ToString("o"),
                                    range_end_utc = SlotTimeUtc.ToString("o"),
                                    reason = rangeResult.Reason,
                                    bar_count = rangeResult.BarCount,
                                    message = "Range computation failed - will retry on next tick or use partial data"
                                }));
                            
                            // Don't commit NO_TRADE - allow retry or partial range computation
                            break;
                        }

                        // Range computed successfully - set values atomically
                        RangeHigh = rangeResult.RangeHigh;
                        RangeLow = rangeResult.RangeLow;
                        FreezeClose = rangeResult.FreezeClose;
                        FreezeCloseSource = rangeResult.FreezeCloseSource;
                        _rangeComputed = true;

                        var computeDuration = (DateTimeOffset.UtcNow - computeStart).TotalMilliseconds;

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
                                duration_ms = computeDuration,
                                // CRITICAL: Log both UTC and Chicago times for auditability
                                first_bar_utc = rangeResult.FirstBarUtc?.ToString("o"),
                                last_bar_utc = rangeResult.LastBarUtc?.ToString("o"),
                                first_bar_chicago = rangeResult.FirstBarChicago?.ToString("o"),
                                last_bar_chicago = rangeResult.LastBarChicago?.ToString("o"),
                                range_start_chicago = RangeStartChicagoTime.ToString("o"),
                                range_end_chicago = SlotTimeChicagoTime.ToString("o"),
                                range_start_utc = RangeStartUtc.ToString("o"),
                                range_end_utc = SlotTimeUtc.ToString("o"),
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
                        Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED", new
                        {
                            range_high = RangeHigh,
                            range_low = RangeLow,
                            range_size = RangeHigh.HasValue && RangeLow.HasValue ? (decimal?)(RangeHigh.Value - RangeLow.Value) : (decimal?)null,
                            freeze_close = FreezeClose,
                            freeze_close_source = FreezeCloseSource
                        });
                        
                        // G) "Nothing happened" explanation: Range locked summary
                        LogSlotEndSummary(utcNow, "RANGE_VALID", true, false, "Range locked, awaiting signal");
                    }
                    else
                    {
                        // Range invalidated - commit and prevent trading
                        LogSlotEndSummary(utcNow, "RANGE_INVALIDATED", false, false, "Range invalidated due to gap tolerance violation");
                        Commit(utcNow, "RANGE_INVALIDATED", "Gap tolerance violation");
                    }

                    // Log range lock snapshot (dry-run)
                    if (_executionMode == ExecutionMode.DRYRUN)
                    {
                        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "DRYRUN_RANGE_LOCK_SNAPSHOT", State.ToString(),
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
                    }

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
                break;

            case StreamState.RANGE_LOCKED:
                // Check for market close cutoff (all execution modes)
                if (!_entryDetected && utcNow >= MarketCloseUtc)
                {
                    LogNoTradeMarketClose(utcNow);
                    Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE");
                }
                break;

            case StreamState.DONE:
                // terminal
                break;
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
        
        // Streams start in PRE_HYDRATION, transition after pre-hydration completes
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

    public void OnBar(DateTimeOffset barUtc, decimal open, decimal high, decimal low, decimal close, DateTimeOffset utcNow)
    {
        // REFACTORED: Do NOT build range incrementally
        // Only buffer bars for retrospective computation when slot time is reached
        if (State == StreamState.RANGE_BUILDING)
        {
            // CRITICAL: Convert bar timestamp to Chicago time explicitly
            // Do NOT assume barUtc is UTC or Chicago - conversion must be explicit
            var barChicagoTime = _time.ConvertUtcToChicago(barUtc);
            
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
                
                lock (_barBufferLock)
                {
                    // Add bar to buffer with actual open price
                    _barBuffer.Add(new Bar(barUtc, open, high, low, close, null));
                }
                
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
                            
                            LogHealth("ERROR", "GAP_TOLERANCE_VIOLATION", $"Range invalidated due to gap violation: {violationReason}",
                                new
                                {
                                    instrument = Instrument,
                                    slot = Stream,
                                    violation_reason = violationReason,
                                    gap_minutes = gapMinutes,
                                    largest_single_gap_minutes = _largestSingleGapMinutes,
                                    total_gap_minutes = _totalGapMinutes,
                                    previous_bar_open_chicago = _lastBarOpenChicago.Value.ToString("o"),
                                    current_bar_open_chicago = barChicagoTime.ToString("o"),
                                    slot_time_chicago = SlotTimeChicagoTime.ToString("o")
                                });
                            
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

        if (_executionMode == ExecutionMode.DRYRUN)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "DRYRUN_BREAKOUT_LEVELS", State.ToString(),
                new
                {
                    brk_long_raw = _brkLongRaw,
                    brk_short_raw = _brkShortRaw,
                    brk_long_rounded = _brkLongRounded,
                    brk_short_rounded = _brkShortRounded,
                    tick_size = _tickSize,
                    rounding_method_name = _spec.breakout.tick_rounding.method
                }));
        }
        else
        {
            // SIM/LIVE mode: log rounded levels (needed for execution)
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
        int liveBarCount;
        lock (_barBufferLock)
        {
            liveBarCount = _barBuffer.Count;
        }
        
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
            lock (_barBufferLock)
            {
                _barBuffer.AddRange(hydratedBars);
                // Sort buffer to maintain chronological order
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
        lock (_barBufferLock)
        {
            bars.AddRange(_barBuffer);
        }

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
        
        foreach (var bar in bars)
        {
            // Capture raw timestamp as received (assumed UTC)
            var barRawUtc = bar.TimestampUtc;
            var barRawUtcKind = barRawUtc.DateTime.Kind.ToString();
            
            // Convert to Chicago time for filtering
            var barChicagoTime = _time.ConvertUtcToChicago(barRawUtc);
            
            // Range window is defined in Chicago time: [RangeStartChicagoTime, endTimeChicagoActual)
            // For hybrid initialization, endTime can be current time (not just slot_time)
            if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < endTimeChicagoActual)
            {
                filteredBars.Add(bar);
                barChicagoTimes[bar] = barChicagoTime; // Cache Chicago time for reuse
                
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
        }
        
        bars = filteredBars;

        if (bars.Count == 0)
        {
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

    private void Transition(DateTimeOffset utcNow, StreamState next, string eventType, object? extra = null)
    {
        State = next;
        _journal.LastState = next.ToString();
        _journal.LastUpdateUtc = utcNow.ToString("o");
        _journals.Save(_journal);
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, eventType, next.ToString(), extra));
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

