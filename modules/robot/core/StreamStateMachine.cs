namespace QTSW2.Robot.Core;

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
    private readonly StreamJournal _journal;
    private readonly decimal _tickSize;
    private readonly string _timetableHash;

    private decimal? _lastCloseBeforeLock;

    public StreamStateMachine(
        TimeService time,
        ParitySpec spec,
        RobotLogger log,
        JournalStore journals,
        string tradingDate,
        string timetableHash,
        TimetableStream directive
    )
    {
        _time = time;
        _spec = spec;
        _log = log;
        _journals = journals;
        _timetableHash = timetableHash;

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

                    ComputeBreakoutLevelsAndLog(utcNow);
                    if (utcNow < MarketCloseUtc)
                        LogIntendedBracketsPlaced(utcNow);
                }
                break;

            case StreamState.RANGE_LOCKED:
                // No orders in skeleton. We only commit at market close (NoTrade) or forced flatten policy (log-only).
                if (utcNow >= MarketCloseUtc)
                {
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

    public void OnBar(DateTimeOffset barUtc, decimal high, decimal low, decimal close, DateTimeOffset utcNow)
    {
        if (State != StreamState.RANGE_BUILDING) return;

        // Track range only for bars that fall within [range_start, slot_time)
        if (barUtc < RangeStartUtc || barUtc >= SlotTimeUtc) return;

        RangeHigh = RangeHigh is null ? high : Math.Max(RangeHigh.Value, high);
        RangeLow = RangeLow is null ? low : Math.Min(RangeLow.Value, low);
        _lastCloseBeforeLock = close;

        // Optional RANGE_UPDATE (throttling left to engine; we won't spam here)
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

        var brkLong = RangeHigh.Value + _tickSize;
        var brkShort = RangeLow.Value - _tickSize;

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "BREAKOUT_LEVELS_COMPUTED", State.ToString(),
            new
            {
                brk_long_unrounded = brkLong,
                brk_short_unrounded = brkShort,
                tick_size = _tickSize,
                rounding_required = true,
                rounding_method = _spec.Breakout.TickRounding.Method
            }));
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

