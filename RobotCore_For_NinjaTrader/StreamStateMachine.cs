using System;

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
        string tradingDate,
        string timetableHash,
        TimetableStream directive,
        ExecutionMode executionMode = ExecutionMode.DRYRUN,
        IExecutionAdapter? executionAdapter = null,
        RiskGate? riskGate = null,
        ExecutionJournal? executionJournal = null
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

        Stream = directive.Stream;
        Instrument = directive.Instrument.ToUpperInvariant();
        Session = directive.Session;
        SlotTimeChicago = directive.SlotTime;

        if (!TimeService.TryParseDateOnly(tradingDate, out var dateOnly))
            throw new InvalidOperationException($"Invalid trading_date '{tradingDate}'");

        var rangeStartChicago = spec.Sessions[Session].RangeStartTime;
        RangeStartUtc = time.ConvertChicagoLocalToUtc(dateOnly, rangeStartChicago);
        SlotTimeUtc = time.ConvertChicagoLocalToUtc(dateOnly, SlotTimeChicago);
        MarketCloseUtc = time.ConvertChicagoLocalToUtc(dateOnly, spec.EntryCutoff.MarketCloseTime);

        if (!spec.TryGetInstrument(Instrument, out var inst))
            throw new InvalidOperationException($"Instrument not found in parity spec: {Instrument}");
        _tickSize = inst.TickSize;
        _baseTarget = inst.BaseTarget;

        var existing = journals.TryLoad(tradingDate, Stream);
        _journal = existing ?? new StreamJournal
        {
            TradingDate = tradingDate,
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
        SlotTimeUtc = _time.ConvertChicagoLocalToUtc(tradingDate, newSlotTimeChicago);
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, "UPDATE_APPLIED", State.ToString(),
            new { slot_time_chicago = SlotTimeChicago, slot_time_utc = SlotTimeUtc.ToString("o") }));
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

        // Recompute UTC times for the new trading_date
        var rangeStartChicago = _spec.Sessions[Session].RangeStartTime;
        RangeStartUtc = _time.ConvertChicagoLocalToUtc(newTradingDate, rangeStartChicago);
        SlotTimeUtc = _time.ConvertChicagoLocalToUtc(newTradingDate, SlotTimeChicago);
        MarketCloseUtc = _time.ConvertChicagoLocalToUtc(newTradingDate, _spec.EntryCutoff.MarketCloseTime);

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
                if (utcNow >= SlotTimeUtc)
                {
                    // Range lock: freeze close is last seen bar close <= slot_time_utc
                    FreezeClose = _lastCloseBeforeLock;
                    FreezeCloseSource = "BAR_CLOSE";

                    Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED", new
                    {
                        range_high = RangeHigh,
                        range_low = RangeLow,
                        range_size = (RangeHigh is not null && RangeLow is not null) ? (decimal?)(RangeHigh.Value - RangeLow.Value) : (decimal?)null,
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
                                range_size = (RangeHigh is not null && RangeLow is not null) ? (decimal?)(RangeHigh.Value - RangeLow.Value) : (decimal?)null,
                                freeze_close = FreezeClose,
                                freeze_close_source = FreezeCloseSource,
                                slot_time_chicago = SlotTimeChicago,
                                slot_time_utc = SlotTimeUtc.ToString("o")
                            }));
                    }

                    ComputeBreakoutLevelsAndLog(utcNow);

                    // Dry-run: Check for immediate entry at lock
                    if (_executionMode == ExecutionMode.DRYRUN && FreezeClose.HasValue && RangeHigh.HasValue && RangeLow.HasValue)
                    {
                        CheckImmediateEntryAtLock(utcNow);
                    }

                    if (utcNow < MarketCloseUtc && _executionMode == ExecutionMode.DRYRUN) // SKELETON mode removed, treat as DRYRUN
                        LogIntendedBracketsPlaced(utcNow);
                }
                break;

            case StreamState.RANGE_LOCKED:
                if (_executionMode == ExecutionMode.DRYRUN && !_entryDetected)
                {
                    // Dry-run: Check for market close cutoff
                    if (utcNow >= MarketCloseUtc)
                    {
                        LogNoTradeMarketClose(utcNow);
                        Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE");
                    }
                }
                // SKELETON mode removed - all execution modes now use same logic
                {
                    // Skeleton: only commit at market close
                    if (utcNow >= MarketCloseUtc)
                    {
                        Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE");
                    }
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

    public void OnBar(DateTimeOffset barUtc, decimal high, decimal low, decimal close, DateTimeOffset utcNow)
    {
        if (State == StreamState.RANGE_BUILDING)
        {
            // Track range only for bars that fall within [range_start, slot_time)
            if (barUtc < RangeStartUtc || barUtc >= SlotTimeUtc) return;

            RangeHigh = RangeHigh is null ? high : Math.Max(RangeHigh.Value, high);
            RangeLow = RangeLow is null ? low : Math.Min(RangeLow.Value, low);
            _lastCloseBeforeLock = close;

            // Optional RANGE_UPDATE (throttling left to engine; we won't spam here)
        }
        else if (State == StreamState.RANGE_LOCKED && _executionMode == ExecutionMode.DRYRUN && !_entryDetected)
        {
            // Dry-run: Check for breakout after lock (before market close)
            if (barUtc >= SlotTimeUtc && barUtc < MarketCloseUtc && _brkLongRounded.HasValue && _brkShortRounded.HasValue)
            {
                CheckBreakoutEntry(barUtc, high, low, utcNow);
            }
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

        // Round using Analyzer-equivalent method
        if (_executionMode == ExecutionMode.DRYRUN)
        {
            _brkLongRounded = UtilityRoundToTick.RoundToTick(_brkLongRaw.Value, _tickSize);
            _brkShortRounded = UtilityRoundToTick.RoundToTick(_brkShortRaw.Value, _tickSize);

            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "DRYRUN_BREAKOUT_LEVELS", State.ToString(),
                new
                {
                    brk_long_raw = _brkLongRaw,
                    brk_short_raw = _brkShortRaw,
                    brk_long_rounded = _brkLongRounded,
                    brk_short_rounded = _brkShortRounded,
                    tick_size = _tickSize,
                    rounding_method_name = _spec.Breakout.TickRounding.Method
                }));
        }
        else
        {
            // Skeleton mode: log unrounded
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "BREAKOUT_LEVELS_COMPUTED", State.ToString(),
                new
                {
                    brk_long_unrounded = _brkLongRaw,
                    brk_short_unrounded = _brkShortRaw,
                    tick_size = _tickSize,
                    rounding_required = true,
                    rounding_method = _spec.Breakout.TickRounding.Method
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
                entry_time_chicago = _time.GetChicagoNow(entryTimeUtc).ToString("o"),
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

    private void Transition(DateTimeOffset utcNow, StreamState next, string eventType, object? extra = null)
    {
        State = next;
        _journal.LastState = next.ToString();
        _journal.LastUpdateUtc = utcNow.ToString("o");
        _journals.Save(_journal);
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, eventType, next.ToString(), extra));
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, "JOURNAL_WRITTEN", next.ToString(),
            new { committed = _journal.Committed, commit_reason = _journal.CommitReason }));
    }
}

