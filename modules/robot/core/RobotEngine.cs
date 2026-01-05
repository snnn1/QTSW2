using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Execution;

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
    private readonly ExecutionMode _executionMode;
    private IExecutionAdapter? _executionAdapter;
    private RiskGate? _riskGate;
    private readonly ExecutionJournal _executionJournal;
    private readonly KillSwitch _killSwitch;
    private readonly ExecutionSummary _executionSummary;

    public RobotEngine(string projectRoot, TimeSpan timetablePollInterval, ExecutionMode executionMode = ExecutionMode.DRYRUN, string? customLogDir = null, string? customTimetablePath = null)
    {
        _root = projectRoot;
        _executionMode = executionMode;
        _log = new RobotLogger(projectRoot, customLogDir);
        _journals = new JournalStore(projectRoot);
        _timetablePoller = new FilePoller(timetablePollInterval);

        _specPath = Path.Combine(_root, "configs", "analyzer_robot_parity.json");
        _timetablePath = customTimetablePath ?? Path.Combine(_root, "data", "timetable", "timetable_current.json");

        // Initialize execution components that don't depend on spec
        _killSwitch = new KillSwitch(projectRoot, _log);
        _executionJournal = new ExecutionJournal(projectRoot, _log);
        _executionSummary = new ExecutionSummary();
    }

    public void Start()
    {
        var utcNow = DateTimeOffset.UtcNow;
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENGINE_START", state: "ENGINE"));

        try
        {
            _spec = ParitySpec.LoadFromFile(_specPath);
            // Debug log: confirm spec_name was loaded
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_NAME_LOADED", state: "ENGINE",
                new { spec_name = _spec.spec_name }));
            _time = new TimeService(_spec.Timezone);
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_LOADED", state: "ENGINE",
                new { spec_name = _spec.spec_name, spec_revision = _spec.spec_revision, timezone = _spec.Timezone }));
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_INVALID", state: "ENGINE", new { error = ex.Message }));
            throw;
        }

        // Initialize execution components now that spec is loaded
        _riskGate = new RiskGate(_spec, _time, _log, _killSwitch);
        _executionAdapter = ExecutionAdapterFactory.Create(_executionMode, _root, _log, _executionJournal);

        // Log execution mode and adapter
        var adapterType = _executionAdapter.GetType().Name;
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_MODE_SET", state: "ENGINE",
            new { mode = _executionMode.ToString(), adapter = adapterType }));

        // Load timetable immediately (fail closed if invalid)
        ReloadTimetableIfChanged(utcNow, force: true);
    }

    public void Stop()
    {
        var utcNow = DateTimeOffset.UtcNow;
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate ?? "", eventType: "ENGINE_STOP", state: "ENGINE"));
        
        // Write execution summary if not DRYRUN
        if (_executionMode != ExecutionMode.DRYRUN)
        {
            var summary = _executionSummary.GetSnapshot();
            var summaryDir = Path.Combine(_root, "data", "execution_summaries");
            Directory.CreateDirectory(summaryDir);
            var summaryPath = Path.Combine(summaryDir, $"summary_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
            var json = JsonUtil.Serialize(summary);
            File.WriteAllText(summaryPath, json);
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate ?? "", eventType: "EXECUTION_SUMMARY_WRITTEN", state: "ENGINE",
                new { summary_path = summaryPath }));
        }
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
        if (_spec is null || _time is null) return;

        // In replay mode, derive trading_date from bar timestamp (bar-derived date is authoritative)
        var barChicagoDate = _time.GetChicagoDateToday(barUtc);
        var barTradingDateStr = barChicagoDate.ToString("yyyy-MM-dd");

        // Check if trading_date needs to roll over to a new day
        if (_activeTradingDate != barTradingDateStr)
        {
            // Trading date rollover: update engine trading_date and notify all streams
            var previousTradingDate = _activeTradingDate;
            _activeTradingDate = barTradingDateStr;

            if (TimeService.TryParseDateOnly(barTradingDateStr, out var newTradingDate))
            {
                // Log trading day rollover
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: barTradingDateStr, eventType: "TRADING_DAY_ROLLOVER", state: "ENGINE",
                    new
                    {
                        previous_trading_date = previousTradingDate ?? "UNSET",
                        new_trading_date = barTradingDateStr,
                        bar_timestamp_utc = barUtc.ToString("o"),
                        bar_timestamp_chicago = _time.GetChicagoNow(barUtc).ToString("o")
                    }));

                // Update all stream state machines to use the new trading_date
                foreach (var stream in _streams.Values)
                {
                    stream.UpdateTradingDate(newTradingDate, utcNow);
                }
            }
        }

        // Replay invariant check: engine trading_date must match bar-derived trading_date
        // This prevents the trading_date mismatch bug from silently reappearing
        if (_activeTradingDate != null && _activeTradingDate != barTradingDateStr)
        {
            // Check if we're in replay mode (timetable has replay metadata)
            var isReplay = false;
            try
            {
                if (File.Exists(_timetablePath))
                {
                    var timetable = TimetableContract.LoadFromFile(_timetablePath);
                    isReplay = timetable.Metadata?.Replay == true;
                }
            }
            catch
            {
                // If we can't determine replay mode, skip invariant check (fail open for live mode)
            }

            if (isReplay)
            {
                // In replay mode, trading_date mismatch after rollover is a critical error
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate ?? "", eventType: "REPLAY_INVARIANT_VIOLATION", state: "ENGINE",
                    new
                    {
                        error = "TRADING_DATE_MISMATCH",
                        engine_trading_date = _activeTradingDate,
                        bar_derived_trading_date = barTradingDateStr,
                        bar_timestamp_utc = barUtc.ToString("o"),
                        bar_timestamp_chicago = _time.GetChicagoNow(barUtc).ToString("o"),
                        message = "Engine trading_date does not match bar-derived trading_date after rollover. Replay aborted."
                    }));
                StandDown();
                return;
            }
        }

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

        var previousHash = _lastTimetableHash;
        _lastTimetableHash = poll.Hash;

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
        
        // For replay timetables (metadata.replay = true), allow trading_date <= chicagoToday
        // For live timetables, require exact match (trading_date == chicagoToday)
        var isReplayTimetable = timetable.Metadata?.Replay == true;
        if (isReplayTimetable)
        {
            if (tradingDate > chicagoToday)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: timetable.TradingDate, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                    new { reason = "REPLAY_TRADING_DATE_FUTURE", trading_date = timetable.TradingDate, chicago_today = chicagoToday.ToString("yyyy-MM-dd") }));
                StandDown();
                return;
            }
        }
        else
        {
            // Live timetable: require exact date match (fail closed on stale timetable)
            if (tradingDate != chicagoToday)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: timetable.TradingDate, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                    new { reason = "STALE_TRADING_DATE", trading_date = timetable.TradingDate, chicago_today = chicagoToday.ToString("yyyy-MM-dd") }));
                StandDown();
                return;
            }
        }

        _activeTradingDate = timetable.TradingDate;

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: timetable.TradingDate, eventType: "TIMETABLE_UPDATED", state: "ENGINE",
            new 
            { 
                previous_hash = previousHash,
                new_hash = _lastTimetableHash,
                enabled_stream_count = timetable.Streams.Count(s => s.Enabled),
                total_stream_count = timetable.Streams.Count
            }));

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: timetable.TradingDate, eventType: "TIMETABLE_LOADED", state: "ENGINE",
            new { streams = timetable.Streams.Count, timetable_hash = _lastTimetableHash, timetable_path = _timetablePath }));
        
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: timetable.TradingDate, eventType: "TIMETABLE_VALIDATED", state: "ENGINE",
            new { trading_date = timetable.TradingDate, is_replay = isReplayTimetable }));

        ApplyTimetable(timetable, tradingDate, utcNow);
    }

    private void ApplyTimetable(TimetableContract timetable, DateOnly tradingDate, DateTimeOffset utcNow)
    {
        if (_spec is null || _time is null || _activeTradingDate is null || _lastTimetableHash is null) return;

        var incoming = timetable.Streams.Where(s => s.Enabled).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var streamIdOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        // Log timetable parsing stats
        int acceptedCount = 0;
        int skippedCount = 0;
        var skippedReasons = new Dictionary<string, int>();

        foreach (var directive in incoming)
        {
            var streamId = directive.Stream;
            
            // Track stream ID occurrences for duplicate detection
            if (!string.IsNullOrWhiteSpace(streamId))
            {
                if (!streamIdOccurrences.TryGetValue(streamId, out var count))
                    count = 0;
                streamIdOccurrences[streamId] = count + 1;
            }
            
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
                skippedCount++;
                if (!skippedReasons.TryGetValue("MISSING_FIELDS", out var count)) count = 0;
                skippedReasons["MISSING_FIELDS"] = count + 1;
                _log.Write(RobotEvents.Base(_time, utcNow, _activeTradingDate, streamId ?? "", instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "MISSING_FIELDS", stream_id = streamId, instrument = instrument, session = session, slot_time = slotTimeChicago }));
                continue;
            }

            if (!_spec.Sessions.ContainsKey(session))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("UNKNOWN_SESSION", out var count2)) count2 = 0;
                skippedReasons["UNKNOWN_SESSION"] = count2 + 1;
                _log.Write(RobotEvents.Base(_time, utcNow, _activeTradingDate, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "UNKNOWN_SESSION", stream_id = streamId, session = session }));
                continue;
            }

            // slot_time validation (fail closed per stream)
            var allowed = _spec.Sessions[session].SlotEndTimes;
            if (!allowed.Contains(slotTimeChicago))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("INVALID_SLOT_TIME", out var count3)) count3 = 0;
                skippedReasons["INVALID_SLOT_TIME"] = count3 + 1;
                _log.Write(RobotEvents.Base(_time, utcNow, _activeTradingDate, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "INVALID_SLOT_TIME", stream_id = streamId, slot_time = slotTimeChicago, allowed_times = allowed }));
                continue;
            }

            if (!_spec.TryGetInstrument(instrument, out _))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("UNKNOWN_INSTRUMENT", out var count4)) count4 = 0;
                skippedReasons["UNKNOWN_INSTRUMENT"] = count4 + 1;
                _log.Write(RobotEvents.Base(_time, utcNow, _activeTradingDate, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "UNKNOWN_INSTRUMENT", stream_id = streamId, instrument = instrument }));
                continue;
            }

            seen.Add(streamId);
            acceptedCount++;

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
                    _log.Write(RobotEvents.Base(_time, utcNow, _activeTradingDate, streamId, instrument, session, sm.SlotTimeChicago, null,
                        "STREAM_UPDATE_SKIPPED", "ENGINE", new 
                        { 
                            reason = "EMPTY_SLOT_TIME",
                            previous_slot_time = sm.SlotTimeChicago,
                            note = "Update skipped due to empty slot_time in timetable"
                        }));
                    continue;
                }
                sm.ApplyDirectiveUpdate(directive.SlotTime, tradingDate, utcNow);
            }
            else
            {
                // New stream
                var newSm = new StreamStateMachine(_time, _spec, _log, _journals, _activeTradingDate, _lastTimetableHash, directive, _executionMode, _executionAdapter, _riskGate, _executionJournal);
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

        // Log duplicate stream IDs if detected
        foreach (var kvp in streamIdOccurrences)
        {
            if (kvp.Value > 1)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: tradingDate.ToString("yyyy-MM-dd"), eventType: "DUPLICATE_STREAM_ID", state: "ENGINE",
                    new
                    {
                        stream_id = kvp.Key,
                        occurrence_count = kvp.Value,
                        note = "Duplicate stream ID detected in timetable - last occurrence will be used"
                    }));
            }
        }

        // Log parsing summary
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: tradingDate.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_PARSING_COMPLETE", state: "ENGINE",
            new { 
                total_enabled = incoming.Count, 
                accepted = acceptedCount, 
                skipped = skippedCount, 
                skipped_reasons = skippedReasons,
                streams_armed = _streams.Count
            }));

        // Any existing streams not present in timetable are left as-is; timetable is authoritative about enabled streams,
        // but the skeleton remains fail-closed (no orders) regardless.
    }

    private void StandDown()
    {
        _streams.Clear();
        _activeTradingDate = null;
    }
}

