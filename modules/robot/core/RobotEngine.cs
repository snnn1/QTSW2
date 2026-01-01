namespace QTSW2.Robot.Core;

public sealed class RobotEngine
{
    private readonly string _root;
    private readonly RobotLogger _log;
    private readonly JournalStore _journals;
    private readonly FilePoller _timetablePoller;

    private ParitySpec? _spec;
    private TimeService? _time;

    private string _specPath = "";
    private string _timetablePath = "";

    private string? _lastTimetableHash;
    private string? _activeTradingDate;

    private readonly Dictionary<string, StreamStateMachine> _streams = new();

    public RobotEngine(string projectRoot, TimeSpan timetablePollInterval)
    {
        _root = projectRoot;
        _log = new RobotLogger(projectRoot);
        _journals = new JournalStore(projectRoot);
        _timetablePoller = new FilePoller(timetablePollInterval);

        _specPath = Path.Combine(_root, "configs", "analyzer_robot_parity.json");
        _timetablePath = Path.Combine(_root, "data", "timetable", "timetable_current.json");
    }

    public void Start()
    {
        var utcNow = DateTimeOffset.UtcNow;
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENGINE_START", state: "ENGINE"));

        try
        {
            _spec = ParitySpec.LoadFromFile(_specPath);
            _time = new TimeService(_spec.Timezone);
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_LOADED", state: "ENGINE",
                new { spec_name = _spec.SpecName, spec_revision = _spec.SpecRevision, timezone = _spec.Timezone }));
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_INVALID", state: "ENGINE", new { error = ex.Message }));
            throw;
        }

        // Load timetable immediately (fail closed if invalid)
        ReloadTimetableIfChanged(utcNow, force: true);
    }

    public void Stop()
    {
        var utcNow = DateTimeOffset.UtcNow;
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate ?? "", eventType: "ENGINE_STOP", state: "ENGINE"));
    }

    public void Tick(DateTimeOffset utcNow)
    {
        if (_spec is null || _time is null) return;

        // Timetable reactivity
        if (_timetablePoller.ShouldPoll(utcNow))
        {
            ReloadTimetableIfChanged(utcNow, force: false);
        }

        foreach (var s in _streams.Values)
            s.Tick(utcNow);
    }

    public void OnBar(DateTimeOffset barUtc, string instrument, decimal high, decimal low, decimal close, DateTimeOffset utcNow)
    {
        // Pass bar data to streams of matching instrument
        foreach (var s in _streams.Values)
        {
            if (s.IsSameInstrument(instrument))
                s.OnBar(barUtc, high, low, close, utcNow);
        }
    }

    private void ReloadTimetableIfChanged(DateTimeOffset utcNow, bool force)
    {
        if (_spec is null || _time is null) return;

        var poll = _timetablePoller.Poll(_timetablePath, utcNow);
        if (poll.Error is not null)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate ?? "", eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new { reason = poll.Error }));
            StandDown();
            return;
        }

        if (!force && !poll.Changed) return;

        _lastTimetableHash = poll.Hash;
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate ?? "", eventType: "TIMETABLE_UPDATED", state: "ENGINE",
            new { timetable_hash = _lastTimetableHash }));

        TimetableContract timetable;
        try
        {
            timetable = TimetableContract.LoadFromFile(_timetablePath);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate ?? "", eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new { reason = "PARSE_ERROR", error = ex.Message }));
            StandDown();
            return;
        }

        if (!TimeService.TryParseDateOnly(timetable.TradingDate, out var tradingDate))
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new { reason = "BAD_TRADING_DATE", trading_date = timetable.TradingDate }));
            StandDown();
            return;
        }

        if (timetable.Timezone != "America/Chicago")
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: timetable.TradingDate, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new { reason = "TIMEZONE_MISMATCH", timezone = timetable.Timezone }));
            StandDown();
            return;
        }

        var chicagoToday = _time.GetChicagoDateToday(utcNow);
        if (tradingDate != chicagoToday)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: timetable.TradingDate, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new { reason = "STALE_TRADING_DATE", trading_date = timetable.TradingDate, chicago_today = chicagoToday.ToString("yyyy-MM-dd") }));
            StandDown();
            return;
        }

        _activeTradingDate = timetable.TradingDate;

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: timetable.TradingDate, eventType: "TIMETABLE_LOADED", state: "ENGINE",
            new { streams = timetable.Streams.Count, timetable_hash = _lastTimetableHash }));

        ApplyTimetable(timetable, tradingDate, utcNow);
    }

    private void ApplyTimetable(TimetableContract timetable, DateOnly tradingDate, DateTimeOffset utcNow)
    {
        if (_spec is null || _time is null || _activeTradingDate is null || _lastTimetableHash is null) return;

        var incoming = timetable.Streams.Where(s => s.Enabled).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directive in incoming)
        {
            var streamId = directive.Stream;
            var instrument = (directive.Instrument ?? "").ToUpperInvariant();
            var session = directive.Session ?? "";
            var slotTimeChicago = directive.SlotTime ?? "";
            DateTimeOffset? slotTimeUtc = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(slotTimeChicago) && TimeService.TryParseDateOnly(_activeTradingDate, out var td))
                    slotTimeUtc = _time.ConvertChicagoLocalToUtc(td, slotTimeChicago);
            }
            catch
            {
                slotTimeUtc = null;
            }

            if (string.IsNullOrWhiteSpace(streamId) ||
                string.IsNullOrWhiteSpace(instrument) ||
                string.IsNullOrWhiteSpace(session) ||
                string.IsNullOrWhiteSpace(slotTimeChicago))
            {
                _log.Write(RobotEvents.Base(_time, utcNow, _activeTradingDate, streamId ?? "", instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "MISSING_FIELDS" }));
                continue;
            }

            if (!_spec.Sessions.ContainsKey(session))
            {
                _log.Write(RobotEvents.Base(_time, utcNow, _activeTradingDate, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "UNKNOWN_SESSION" }));
                continue;
            }

            // slot_time validation (fail closed per stream)
            var allowed = _spec.Sessions[session].SlotEndTimes;
            if (!allowed.Contains(slotTimeChicago))
            {
                _log.Write(RobotEvents.Base(_time, utcNow, _activeTradingDate, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "INVALID_SLOT_TIME" }));
                continue;
            }

            if (!_spec.TryGetInstrument(instrument, out _))
            {
                _log.Write(RobotEvents.Base(_time, utcNow, _activeTradingDate, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "UNKNOWN_INSTRUMENT" }));
                continue;
            }

            seen.Add(streamId);

            if (_streams.TryGetValue(streamId, out var sm))
            {
                if (sm.Committed)
                {
                    _log.Write(RobotEvents.Base(_time, utcNow, _activeTradingDate, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                        "UPDATE_IGNORED_COMMITTED", "ENGINE", new { reason = "STREAM_COMMITTED" }));
                    continue;
                }

                // Apply updates only for uncommitted streams
                if (string.IsNullOrWhiteSpace(directive.SlotTime))
                {
                    // Fail closed: skip update if slot_time is null/empty
                    continue;
                }
                sm.ApplyDirectiveUpdate(directive.SlotTime, tradingDate, utcNow);
            }
            else
            {
                // New stream
                var newSm = new StreamStateMachine(_time, _spec, _log, _journals, _activeTradingDate, _lastTimetableHash, directive);
                _streams[streamId] = newSm;

                if (newSm.Committed)
                {
                    _log.Write(RobotEvents.Base(_time, utcNow, _activeTradingDate, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                        "STREAM_SKIPPED", "ENGINE", new { reason = "ALREADY_COMMITTED_JOURNAL" }));
                    continue;
                }

                newSm.Arm(utcNow);
            }
        }

        // Any existing streams not present in timetable are left as-is; timetable is authoritative about enabled streams,
        // but the skeleton remains fail-closed (no orders) regardless.
    }

    private void StandDown()
    {
        _streams.Clear();
        _activeTradingDate = null;
    }
}

