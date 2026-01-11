using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Execution;

public enum StreamState
{
    IDLE,
    ARMED,
    RANGE_BUILDING,
    RANGE_LOCKED,
    DONE,
    RECOVERY_MANAGE
}

public sealed class StreamStateMachine
{
    public string Stream { get; }
    public string Instrument { get; }
    public string Session { get; }
    public string SlotTimeChicago { get; private set; }

    public StreamState State { get; private set; } = StreamState.IDLE;
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
    private readonly IBarProvider? _barProvider; // Optional: for DRYRUN mode to query historical bars

    // Bar buffer for retrospective range computation (live mode)
    private readonly List<Bar> _barBuffer = new();
    private readonly object _barBufferLock = new();
    private bool _rangeComputed = false; // Flag to prevent multiple range computations
    private DateTimeOffset? _lastSlotGateDiagnostic = null; // Rate-limiting for SLOT_GATE_DIAGNOSTIC
    private DateTimeOffset? _lastBarDiagnosticTime = null; // Rate-limiting for BAR_RECEIVED_DIAGNOSTIC
    private const int BAR_DIAGNOSTIC_RATE_LIMIT_SECONDS = 30; // Log once per 30 seconds (reduced for debugging)
    
    // Assertion flags (once per stream per day)
    private bool _rangeIntentAssertEmitted = false; // RANGE_INTENT_ASSERT emitted
    private bool _firstBarAcceptedAssertEmitted = false; // RANGE_FIRST_BAR_ACCEPTED emitted
    private bool _rangeLockAssertEmitted = false; // RANGE_LOCK_ASSERT emitted
    private bool _hydrationAttempted = false; // Prevent multiple hydration attempts

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
        IBarProvider? barProvider = null // Optional: for DRYRUN mode to query historical bars
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
        _barProvider = barProvider;

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
        
        // Log diagnostic info (only once per stream creation, not on every update)
        _log.Write(RobotEvents.Base(time, DateTimeOffset.UtcNow, tradingDateStr, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "STREAM_INIT_DIAGNOSTIC", State.ToString(),
            new
            {
                trading_date_string = tradingDateStr,
                trading_date_parsed = dateOnly.ToString("yyyy-MM-dd"),
                range_start_chicago = rangeStartChicago,
                slot_time_chicago = SlotTimeChicago,
                range_start_chicago_time = RangeStartChicagoTime.ToString("o"),
                slot_time_chicago_time = SlotTimeChicagoTime.ToString("o"),
                range_start_utc = RangeStartUtc.ToString("o"),
                slot_time_utc = SlotTimeUtc.ToString("o"),
                note = "Logging trading date and calculated slot times at stream initialization (Chicago time is authoritative)"
            }));

        if (!spec.TryGetInstrument(Instrument, out var inst))
            throw new InvalidOperationException($"Instrument not found in parity spec: {Instrument}");
        _tickSize = inst.tick_size;
        _baseTarget = inst.base_target;

        // PHASE 3: Convert DateOnly to string for journal operations (journal stores as string)
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
        _brkLongRaw = null;
        _brkShortRaw = null;
        _brkLongRounded = null;
        _brkShortRounded = null;
        _rangeComputed = false;
        
        // Reset assertion flags for new trading day
        _rangeIntentAssertEmitted = false;
        _firstBarAcceptedAssertEmitted = false;
        _rangeLockAssertEmitted = false;
        _hydrationAttempted = false;
        
        // Clear bar buffer on reset
        lock (_barBufferLock)
        {
            _barBuffer.Clear();
        }

        // Reset state to ARMED if we were in a mid-day state (preserve committed state)
        if (!_journal.Committed && State != StreamState.IDLE && State != StreamState.ARMED)
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
            case StreamState.IDLE:
                // Engine decides when to arm; no-op here.
                break;

            case StreamState.ARMED:
                if (utcNow >= RangeStartUtc)
                {
                    Transition(utcNow, StreamState.RANGE_BUILDING, "RANGE_BUILD_START");
                }
                break;

            case StreamState.RANGE_BUILDING:
                // SAFETY CHECK: If robot started after slot time passed, don't compute ranges or trade
                // This prevents trading when robot wasn't running during the range window
                if (utcNow >= SlotTimeUtc && !_rangeComputed)
                {
                    lock (_barBufferLock)
                    {
                        var barCount = _barBuffer.Count;
                        
                        // If slot time has passed AND we have no bars, robot started too late
                        // Don't attempt range computation or trading
                        if (barCount == 0)
                        {
                            var nowChicagoSkip = _time.ConvertUtcToChicago(utcNow);
                            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                                "LATE_START_SKIP", State.ToString(),
                                new
                                {
                                    now_utc = utcNow.ToString("o"),
                                    now_chicago = nowChicagoSkip.ToString("o"),
                                    slot_time_chicago = SlotTimeChicago,
                                    slot_time_utc = SlotTimeUtc.ToString("o"),
                                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                                    bar_buffer_count = barCount,
                                    reason = "Robot started after slot time passed with no bars buffered - skipping range computation and trading",
                                    message = "Robot was not running during range window - fail-closed safety check"
                                }));
                            
                            // Commit stream as NO_TRADE due to late start
                            Commit(utcNow, "NO_TRADE_LATE_START", "LATE_START_SKIP");
                            break;
                        }
                    }
                }
                
                // Hydrate from historical bars if starting late (only once, when entering RANGE_BUILDING)
                // Only attempt hydration if slot time hasn't passed yet, or if we have bars buffered
                if (!_rangeComputed && !_hydrationAttempted && _barProvider != null && utcNow < SlotTimeUtc)
                {
                    _hydrationAttempted = true;
                    TryHydrateFromHistory(utcNow);
                }
                
                // DIAGNOSTIC: Log slot gate evaluation (rate-limited to once per minute per stream)
                var nowChicago = _time.ConvertUtcToChicago(utcNow);
                var gateDecision = utcNow >= SlotTimeUtc && !_rangeComputed;
                var comparisonUsed = $"utcNow ({utcNow:o}) >= SlotTimeUtc ({SlotTimeUtc:o}) && !_rangeComputed ({!_rangeComputed})";
                
                // Rate-limit diagnostic logging (once per 30 seconds, or immediately when gate passes)
                if (!_lastSlotGateDiagnostic.HasValue || (utcNow - _lastSlotGateDiagnostic.Value).TotalSeconds >= 30 || gateDecision)
                {
                    _lastSlotGateDiagnostic = utcNow;
                    lock (_barBufferLock)
                    {
                        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "SLOT_GATE_DIAGNOSTIC", State.ToString(),
                            new
                            {
                                now_utc = utcNow.ToString("o"),
                                now_chicago = nowChicago.ToString("o"),
                                slot_time_chicago = SlotTimeChicago,
                                slot_time_utc = SlotTimeUtc.ToString("o"),
                                comparison_used = comparisonUsed,
                                decision_result = gateDecision,
                                stream_id = Stream,
                                trading_date = TradingDate,
                                range_computed_flag = _rangeComputed,
                                bar_buffer_count = _barBuffer.Count,
                                time_until_slot_seconds = gateDecision ? 0 : (SlotTimeUtc - utcNow).TotalSeconds
                            }));
                    }
                }
                
                if (utcNow >= SlotTimeUtc && !_rangeComputed)
                {
                    // REFACTORED: Compute range retrospectively from completed session data
                    // Do NOT build incrementally - treat session window as closed dataset
                    var computeStart = DateTimeOffset.UtcNow;
                    
                    // Log time conversion method info once per stream
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
                            bar_buffer_count = _barBuffer.Count,
                            note = "Range is calculated for Chicago time window (UTC times shown for reference)"
                        }));

                    var rangeResult = ComputeRangeRetrospectively(utcNow);
                    
                    if (!rangeResult.Success)
                    {
                        // Range data missing - mark stream as NO_TRADE for the day
                        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "RANGE_DATA_MISSING", State.ToString(),
                            new
                            {
                                range_start_utc = RangeStartUtc.ToString("o"),
                                range_end_utc = SlotTimeUtc.ToString("o"),
                                reason = rangeResult.Reason,
                                bar_count = rangeResult.BarCount,
                                message = "Full range window data unavailable - stream marked NO_TRADE"
                            }));
                        
                        // Commit stream as NO_TRADE
                        Commit(utcNow, "NO_TRADE_RANGE_DATA_MISSING", "RANGE_DATA_MISSING");
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
                            range_end_utc = SlotTimeUtc.ToString("o")
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

                    // Transition to RANGE_LOCKED (range is guaranteed to be non-null)
                    Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED", new
                    {
                        range_high = RangeHigh,
                        range_low = RangeLow,
                        range_size = RangeHigh.HasValue && RangeLow.HasValue ? (decimal?)(RangeHigh.Value - RangeLow.Value) : (decimal?)null,
                        freeze_close = FreezeClose,
                        freeze_close_source = FreezeCloseSource
                    });

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

            case StreamState.RECOVERY_MANAGE:
                // Skeleton: no account interaction. Engine will decide if it can resume.
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
        if (State == StreamState.IDLE)
            Transition(utcNow, StreamState.ARMED, "STREAM_ARMED");
    }

    public void EnterRecoveryManage(DateTimeOffset utcNow, string reason)
    {
        if (_journal.Committed)
        {
            State = StreamState.DONE;
            return;
        }
        Transition(utcNow, StreamState.RECOVERY_MANAGE, "RECOVERY_MANAGE", new { reason });
    }

    private DateTimeOffset _lastExecutionGateEvalBarUtc = DateTimeOffset.MinValue;
    private const int EXECUTION_GATE_EVAL_RATE_LIMIT_SECONDS = 60; // Log once per minute max

    public void OnBar(DateTimeOffset barUtc, decimal high, decimal low, decimal close, DateTimeOffset utcNow)
    {
        // REFACTORED: Do NOT build range incrementally
        // Only buffer bars for retrospective computation when slot time is reached
        if (State == StreamState.RANGE_BUILDING)
        {
            // CRITICAL: Convert bar timestamp to Chicago time explicitly
            // Do NOT assume barUtc is UTC or Chicago - conversion must be explicit
            var barChicagoTime = _time.ConvertUtcToChicago(barUtc);
            
            // DIAGNOSTIC: Log bar reception details (rate-limited)
            var shouldLogBar = !_lastBarDiagnosticTime.HasValue || 
                              (utcNow - _lastBarDiagnosticTime.Value).TotalSeconds >= BAR_DIAGNOSTIC_RATE_LIMIT_SECONDS;
            
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
                    _barBuffer.Add(new Bar(barUtc, close, high, low, close, null));
                }
            }
            else
            {
                // DIAGNOSTIC: Log bars that are filtered out (rate-limited, only when close to window)
                var timeUntilStart = (RangeStartChicagoTime - barChicagoTime).TotalMinutes;
                var timeAfterEnd = (barChicagoTime - SlotTimeChicagoTime).TotalMinutes;
                if (shouldLogBar && (Math.Abs(timeUntilStart) < 30 || Math.Abs(timeAfterEnd) < 30))
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
            // Bars at/after slot time are for breakout detection (handled in RANGE_LOCKED state)
        }
        else if (State == StreamState.RANGE_LOCKED)
        {
            // DIAGNOSTIC: Log execution gate evaluation (rate-limited)
            var barChicago = _time.ConvertUtcToChicago(barUtc);
            var timeSinceLastEval = (barUtc - _lastExecutionGateEvalBarUtc).TotalSeconds;
            if (timeSinceLastEval >= EXECUTION_GATE_EVAL_RATE_LIMIT_SECONDS || _lastExecutionGateEvalBarUtc == DateTimeOffset.MinValue)
            {
                _lastExecutionGateEvalBarUtc = barUtc;
                LogExecutionGateEval(barUtc, barChicago, utcNow);
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
        var streamArmed = State != StreamState.IDLE;
        var stateOk = State == StreamState.RANGE_LOCKED;
        var entryDetectionModeOk = true; // FIXED: Now works for all modes
        var executionModeOk = _executionMode != ExecutionMode.DRYRUN; // SIM/LIVE can execute
        
        // Check if we can detect entries (this was the bug - only DRYRUN could detect)
        var canDetectEntries = stateOk && !_entryDetected && slotReached && 
                               barUtc < MarketCloseUtc && 
                               _brkLongRounded.HasValue && _brkShortRounded.HasValue;
        
        // Final allowed: all gates must pass
        var finalAllowed = realtimeOk && 
                          !string.IsNullOrEmpty(tradingDay) &&
                          sessionActive &&
                          slotReached &&
                          timetableEnabled &&
                          streamArmed &&
                          stateOk &&
                          entryDetectionModeOk &&
                          executionModeOk &&
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
                execution_mode_ok = executionModeOk,
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
        
        if (barUtc >= slotTimePlusInterval && !finalAllowed && executionModeOk && stateOk && slotReached)
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
    /// REFACTORED: Compute range retrospectively from completed session data.
    /// Queries all bars in [RangeStartChicagoTime, SlotTimeChicagoTime) and computes range in one pass.
    /// CRITICAL: Bar filtering uses Chicago time, not UTC, to match trading session semantics.
    /// </summary>
    private (bool Success, decimal? RangeHigh, decimal? RangeLow, decimal? FreezeClose, string FreezeCloseSource, int BarCount, string? Reason, DateTimeOffset? FirstBarUtc, DateTimeOffset? LastBarUtc, DateTimeOffset? FirstBarChicago, DateTimeOffset? LastBarChicago) ComputeRangeRetrospectively(DateTimeOffset utcNow)
    {
        var bars = new List<Bar>();
        
        // Try to get bars from IBarProvider (DRYRUN mode) or bar buffer (live mode)
        if (_barProvider != null)
        {
            // DRYRUN mode: Query bars from provider (provider uses UTC, we'll filter by Chicago time)
            try
            {
                bars.AddRange(_barProvider.GetBars(Instrument, RangeStartUtc, SlotTimeUtc));
            }
            catch (Exception ex)
            {
                return (false, null, null, null, "UNSET", 0, $"BAR_PROVIDER_ERROR: {ex.Message}", null, null, null, null);
            }
        }
        else
        {
            // Live mode: Use buffered bars
            lock (_barBufferLock)
            {
                bars.AddRange(_barBuffer);
            }
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
            // DIAGNOSTIC: Capture raw timestamp as received (assumed UTC)
            // bar.TimestampUtc is what we receive - we assume it's UTC but need to verify
            var barRawUtc = bar.TimestampUtc;
            var barRawUtcKind = barRawUtc.DateTime.Kind.ToString();
            
            // Convert to Chicago time for filtering
            // DIAGNOSTIC: This conversion assumes bar.TimestampUtc is UTC
            var barChicagoTime = _time.ConvertUtcToChicago(barRawUtc);
            
            // Range window is defined in Chicago time: [RangeStartChicagoTime, SlotTimeChicagoTime)
            if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime)
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
        
        // DIAGNOSTIC: Log RANGE_WINDOW_AUDIT with timestamp conversion details
        if (firstBarRawUtc.HasValue && lastBarRawUtc.HasValue)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_WINDOW_AUDIT", State.ToString(),
                new
                {
                    first_bar_raw_nt_time = firstBarRawUtc.Value.ToString("o"),
                    first_bar_raw_nt_kind = firstBarRawUtcKind,
                    first_bar_assumed_utc = firstBarRawUtc.Value.ToString("o"),
                    first_bar_assumed_utc_kind = firstBarRawUtc.Value.DateTime.Kind.ToString(),
                    first_bar_chicago = firstBarChicago?.ToString("o"),
                    first_bar_chicago_offset = firstBarChicago?.Offset.ToString(),
                    last_bar_raw_nt_time = lastBarRawUtc.Value.ToString("o"),
                    last_bar_raw_nt_kind = lastBarRawUtcKind,
                    last_bar_assumed_utc = lastBarRawUtc.Value.ToString("o"),
                    last_bar_assumed_utc_kind = lastBarRawUtc.Value.DateTime.Kind.ToString(),
                    last_bar_chicago = lastBarChicago?.ToString("o"),
                    last_bar_chicago_offset = lastBarChicago?.Offset.ToString(),
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
            
            // Find last bar close before slot time (for freeze close)
            // Use Chicago time comparison to match trading session semantics
            // OPTIMIZATION: Reuse cached Chicago time instead of converting again
            var barChicagoTime = barChicagoTimes[bar];
            if (barChicagoTime < SlotTimeChicagoTime)
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
        // Reuse diagnostic variables (already set to first/last filtered bars)
        var firstBarUtc = bars[0].TimestampUtc;
        var lastBarUtc = bars[bars.Count - 1].TimestampUtc;
        // firstBarChicago and lastBarChicago are already set during filtering above

        return (true, rangeHigh, rangeLow, freezeClose, "BAR_CLOSE", bars.Count, null, firstBarUtc, lastBarUtc, firstBarChicago, lastBarChicago);
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

        // Execution logic: only proceed if mode != DRYRUN
        if (_executionMode == ExecutionMode.DRYRUN)
        {
            // DRYRUN: Only log, no execution
            return;
        }

        // For SIM/LIVE: Build intent and execute
        if (_intendedStopPrice == null || _intendedTargetPrice == null || _intendedBeTrigger == null)
        {
            // Intent incomplete - cannot execute
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "EXECUTION_SKIPPED", State.ToString(),
                new { reason = "INTENT_INCOMPLETE", direction, entry_price = entryPrice }));
            return;
        }

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
            streamArmed: State != StreamState.IDLE,
            completenessFlag: "COMPLETE",
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
            // Use reflection or type check to access adapter-specific method
            var adapterType = _executionAdapter?.GetType();
            if (adapterType?.Name == "NinjaTraderSimAdapter")
            {
                var registerMethod = adapterType.GetMethod("RegisterIntent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                registerMethod?.Invoke(_executionAdapter, new object[] { intent });
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
    /// Hydrate bar buffer from historical bars if starting late.
    /// Called when entering RANGE_BUILDING state and current time is after range start.
    /// </summary>
    private void TryHydrateFromHistory(DateTimeOffset utcNow)
    {
        // Only hydrate if:
        // 1. Bar provider is available (for historical data access)
        // 2. Range has not been computed
        // 3. Current time is after range start (we're starting late)
        // 4. Current time is before slot time (not too late)
        if (_barProvider == null || _rangeComputed)
            return;

        var nowChicago = _time.ConvertUtcToChicago(utcNow);
        
        // Check if we're starting late (current time > range start)
        if (nowChicago < RangeStartChicagoTime)
            return; // Not late yet, wait for normal bar flow

        // Check if slot time has passed (too late to hydrate)
        if (nowChicago >= SlotTimeChicagoTime)
            return; // Slot time passed, range computation will handle it

        // Check if buffer is empty or incomplete
        int currentBarCount;
        lock (_barBufferLock)
        {
            currentBarCount = _barBuffer.Count;
        }

        // Try to load historical bars to ensure completeness
        try
        {
            // Get historical bars from provider (provider uses UTC)
            var historicalBars = _barProvider.GetBars(Instrument, RangeStartUtc, SlotTimeUtc).ToList();
            
            if (historicalBars.Count == 0)
                return; // No historical bars available

            // Filter bars to Chicago time window and add to buffer
            var hydratedCount = 0;
            DateTimeOffset? firstBarChicago = null;
            DateTimeOffset? lastBarChicago = null;

            lock (_barBufferLock)
            {
                // Create a set of existing bar timestamps to avoid duplicates
                var existingTimestamps = new HashSet<DateTimeOffset>(_barBuffer.Select(b => b.TimestampUtc));

                foreach (var bar in historicalBars)
                {
                    // Skip if already in buffer
                    if (existingTimestamps.Contains(bar.TimestampUtc))
                        continue;

                    // Convert to Chicago time and check if in window
                    var barChicagoTime = _time.ConvertUtcToChicago(bar.TimestampUtc);
                    
                    // Only include bars in the range window
                    if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime)
                    {
                        _barBuffer.Add(bar);
                        existingTimestamps.Add(bar.TimestampUtc);
                        hydratedCount++;

                        if (!firstBarChicago.HasValue || barChicagoTime < firstBarChicago.Value)
                            firstBarChicago = barChicagoTime;
                        if (!lastBarChicago.HasValue || barChicagoTime > lastBarChicago.Value)
                            lastBarChicago = barChicagoTime;
                    }
                }

                // Sort buffer by timestamp to maintain chronological order
                _barBuffer.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
            }

            // Log hydration event (once per stream per day)
            if (hydratedCount > 0)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "RANGE_HYDRATED_FROM_HISTORY", State.ToString(),
                    new
                    {
                        trading_date = TradingDate,
                        bars_loaded_count = hydratedCount,
                        first_bar_chicago = firstBarChicago?.ToString("o"),
                        last_bar_chicago = lastBarChicago?.ToString("o"),
                        total_bars_in_buffer = currentBarCount + hydratedCount,
                        note = "late start hydration"
                    }));
            }
        }
        catch (Exception ex)
        {
            // Log error but don't fail - continue with normal bar processing
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_HYDRATION_ERROR", State.ToString(),
                new
                {
                    error = ex.Message,
                    note = "Historical hydration failed, continuing with normal bar processing"
                }));
        }
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

