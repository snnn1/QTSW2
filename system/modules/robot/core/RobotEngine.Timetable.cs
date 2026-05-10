using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Execution;
using QTSW2.Robot.Core.SessionAuthority;

public sealed partial class RobotEngine
{
    private (FilePollResult Poll, TimetableContract? Timetable, Exception? ParseException) PollAndParseTimetable(DateTimeOffset utcNow)
    {
        _timetablePoller.MarkPolled(utcNow);

        if (!File.Exists(_timetablePath))
        {
            return (new FilePollResult(false, null, "MISSING"), null, null);
        }

        var (hash, timetable, changed, wasCacheHit) = TimetableCache.GetOrLoad(_timetablePath, _lastTimetableHash);

        // Instrumentation: rate-limited TIMETABLE_CACHE_HIT / TIMETABLE_CACHE_REFRESH for validation
        if (wasCacheHit)
        {
            var shouldLog = !_lastTimetableCacheHitLogUtc.HasValue ||
                (utcNow - _lastTimetableCacheHitLogUtc.Value).TotalSeconds >= TIMETABLE_CACHE_LOG_RATE_LIMIT_SECONDS;
            if (shouldLog)
            {
                _lastTimetableCacheHitLogUtc = utcNow;
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_CACHE_HIT", state: "ENGINE",
                    new { note = "Timetable served from cache (no disk read)" }));
            }
        }
        else
        {
            _lastTimetableCacheRefreshLogUtc = utcNow;
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_CACHE_REFRESH", state: "ENGINE",
                new { note = "Timetable refreshed from disk (cache miss or file changed)" }));
        }

        // Parse error: GetOrLoad returns (rawHash, null, true, false) when LoadFromBytes fails
        if (timetable is null && hash is not null)
        {
            return (new FilePollResult(true, hash, "PARSE_ERROR"), null, new InvalidOperationException("Timetable parse failed"));
        }
        if (timetable is null && hash is null)
        {
            return (new FilePollResult(false, null, "MISSING"), null, null);
        }

        return (new FilePollResult(changed, hash, null), timetable, null);
    }

    /// <summary>
    /// Live timetables require <c>data/session/session_authority.json</c> with strict YYYY-MM-DD <c>session_trading_date</c> matching the timetable (post-clamp).
    /// Replay timetable: skipped entirely.
    /// </summary>
    private bool TryValidateSessionAuthorityMatchesTimetable(DateTimeOffset utcNow, string timetableSessionTradingDate, bool timetableReplay)
    {
        if (timetableReplay)
            return true;

        var authorityPath = Path.Combine(_root, "data", "session", "session_authority.json");
        var canonicalSession = ComputeCmeTradingDateString(utcNow);

        if (!File.Exists(authorityPath))
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_SESSION_AUTHORITY_INVALID", state: "CRITICAL",
                new
                {
                    reason = "authority_file_missing",
                    timetable_session = timetableSessionTradingDate,
                    authority_session = (string?)null,
                    authority_mode = (string?)null,
                    canonical_session = canonicalSession,
                    file_path = authorityPath,
                    timetable_path = _timetablePath,
                    note = "Live execution requires persisted session_authority.json"
                }));
            StandDown();
            return false;
        }

        string json;
        try
        {
            json = File.ReadAllText(authorityPath);
        }
        catch (Exception ex)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_SESSION_AUTHORITY_INVALID", state: "CRITICAL",
                new
                {
                    reason = "read_failed",
                    error = ex.Message,
                    timetable_session = timetableSessionTradingDate,
                    authority_session = (string?)null,
                    authority_mode = (string?)null,
                    canonical_session = canonicalSession,
                    file_path = authorityPath,
                    timetable_path = _timetablePath
                }));
            StandDown();
            return false;
        }

        SessionAuthorityContract? authority;
        try
        {
            authority = JsonUtil.Deserialize<SessionAuthorityContract>(json);
        }
        catch (Exception ex)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_SESSION_AUTHORITY_INVALID", state: "CRITICAL",
                new
                {
                    reason = "parse_failed",
                    error = ex.Message,
                    timetable_session = timetableSessionTradingDate,
                    authority_session = (string?)null,
                    authority_mode = (string?)null,
                    canonical_session = canonicalSession,
                    file_path = authorityPath,
                    timetable_path = _timetablePath
                }));
            StandDown();
            return false;
        }

        var authorityMode = string.IsNullOrWhiteSpace(authority?.mode) ? (string?)null : authority!.mode!.Trim();

        var authSession = (authority?.session_trading_date ?? "").Trim();
        if (string.IsNullOrEmpty(authSession))
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_SESSION_AUTHORITY_INVALID", state: "CRITICAL",
                new
                {
                    reason = "missing_or_empty_authority_session",
                    timetable_session = timetableSessionTradingDate,
                    authority_session = "",
                    authority_mode = authorityMode,
                    canonical_session = canonicalSession,
                    file_path = authorityPath,
                    timetable_path = _timetablePath,
                    note = "session_authority.json exists but session_trading_date is missing or empty"
                }));
            StandDown();
            return false;
        }

        if (!SessionAuthorityTimetableGate.IsStrictIsoDate(authSession))
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_SESSION_AUTHORITY_INVALID", state: "CRITICAL",
                new
                {
                    reason = "invalid_format",
                    timetable_session = timetableSessionTradingDate,
                    authority_session = authSession,
                    authority_mode = authorityMode,
                    canonical_session = canonicalSession,
                    file_path = authorityPath,
                    timetable_path = _timetablePath,
                    note = "session_trading_date must be strict YYYY-MM-DD (zero-padded) and a valid calendar day"
                }));
            StandDown();
            return false;
        }

        if (!string.Equals(timetableSessionTradingDate, authSession, StringComparison.Ordinal))
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_AUTHORITY_SESSION_MISMATCH", state: "CRITICAL",
                new
                {
                    reason = "session_mismatch",
                    timetable_session = timetableSessionTradingDate,
                    authority_session = authSession,
                    authority_mode = authorityMode,
                    canonical_session = canonicalSession,
                    file_path = authorityPath,
                    timetable_path = _timetablePath,
                    note = "Persisted SessionAuthority session_trading_date does not match timetable after parse/clamp"
                }));
            StandDown();
            return false;
        }

        return true;
    }

    private void ReloadTimetableIfChanged(
        DateTimeOffset utcNow,
        bool force,
        FilePollResult poll,
        TimetableContract? timetable,
        Exception? parseException)
    {
        if (_spec is null || _time is null) return;

        if (poll.Error is not null)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new
                {
                    reason = "POLL_ERROR",
                    error = poll.Error,
                    trading_date = TradingDateString,
                    timetable_path = _timetablePath,
                    note = "Timetable file poll failed - engine will stand down"
                }));
            StandDown();
            return;
        }

        // Only proceed if timetable actually changed (or forced)
        if (!force && !poll.Changed) return;

        var previousHash = _lastTimetableHash;
        var timetableActuallyChanged = poll.Changed && previousHash != null;
        _lastTimetableHash = poll.Hash;

        if (timetable is null)
        {
            var err = parseException?.Message ?? "Unknown timetable parse error";
            var errType = parseException?.GetType().Name ?? "Unknown";
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new
                {
                    reason = "PARSE_ERROR",
                    error = err,
                    error_type = errType,
                    trading_date = TradingDateString,
                    timetable_path = _timetablePath,
                    note = "Timetable file parse failed - engine will stand down"
                }));
            StandDown();
            return;
        }

        // Lock trading date from timetable (session_trading_date preferred in JSON)
        var timetableSessionRaw = (timetable.session_trading_date ?? "").Trim();
        if (string.IsNullOrWhiteSpace(timetableSessionRaw))
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_MISSING_TRADING_DATE", state: "ENGINE",
                new { reason = "Timetable exists but session_trading_date is missing or empty", timetable_path = _timetablePath }));
            StandDown();
            return;
        }

        // Parse session date from timetable (format: "YYYY-MM-DD")
        DateOnly? timetableTradingDate = null;
        try
        {
            timetableTradingDate = DateOnly.Parse(timetableSessionRaw);
        }
        catch (Exception ex)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_INVALID_TRADING_DATE", state: "ENGINE",
                new { reason = "Failed to parse session_trading_date", session_trading_date = timetableSessionRaw, error = ex.Message, timetable_path = _timetablePath }));
            StandDown();
            return;
        }

        // Live: timetable may list the next calendar session day before 18:00 CT (early publish). Canonical day
        // matches modules.timetable.cme_session.get_cme_trading_date — clamp in-memory contract so lock, eligibility,
        // and ApplyTimetable agree (avoids SESSION_START_DATE_MISMATCH stand-down + empty GetTradingDate()).
        if (timetable.metadata?.replay != true)
        {
            var expectedCmeStrForClamp = ComputeCmeTradingDateString(utcNow);
            if (DateOnly.TryParse(expectedCmeStrForClamp, out var expectedCmeDateForClamp) &&
                timetableTradingDate.Value == expectedCmeDateForClamp.AddDays(1) &&
                IsChicagoWallHourStrictlyBeforeCmeRollover(utcNow))
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: expectedCmeStrForClamp, eventType: "SESSION_START_DATE_TIMETABLE_AHEAD_CLAMPED", state: "ENGINE",
                    new
                    {
                        expected_trading_date = expectedCmeStrForClamp,
                        file_session_trading_date = timetableSessionRaw,
                        note = "session_trading_date was one day ahead of CME session before 18:00 CT; clamped to canonical date"
                    }));
                timetable.session_trading_date = expectedCmeStrForClamp;
                timetableTradingDate = expectedCmeDateForClamp;
                timetableSessionRaw = expectedCmeStrForClamp;
            }
        }

        if (!TryValidateSessionAuthorityMatchesTimetable(utcNow, timetableSessionRaw, timetable.metadata?.replay == true))
            return;

        // Lock trading date if not already set
        bool dateWasLocked = false;
        bool timetableDateRolledForward = false;
        if (!_activeTradingDate.HasValue)
        {
            if (timetable.metadata?.replay != true)
            {
                var expectedCme = ComputeCmeTradingDateString(utcNow);
                if (!string.Equals(timetableSessionRaw, expectedCme, StringComparison.Ordinal))
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: expectedCme, eventType: "SESSION_START_DATE_MISMATCH", state: "CRITICAL",
                        new
                        {
                            expected_trading_date = expectedCme,
                            timetable_trading_date = timetableSessionRaw,
                            reason = "startup_session_mismatch"
                        }));
                    StandDown();
                    return;
                }
            }

            _activeTradingDate = timetableTradingDate.Value;
            dateWasLocked = true;

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: timetableTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "TRADING_DATE_LOCKED", state: "ENGINE",
                new
                {
                    trading_date = timetableTradingDate.Value.ToString("yyyy-MM-dd"),
                    source = "TIMETABLE",
                    timetable_path = _timetablePath,
                    note = "Trading date locked from timetable; rolls forward when timetable trading_date advances (TRADING_DATE_ROLLED_FORWARD)"
                }));
        }
        else if (_activeTradingDate.Value != timetableTradingDate.Value)
        {
            if (timetableTradingDate.Value > _activeTradingDate.Value)
            {
                var previousLocked = _activeTradingDate.Value;
                _activeTradingDate = timetableTradingDate.Value;
                var clearedStreams = _streams.Count;
                var retainedStreams = 0;
                var retention = RetainTimetableRolloverStreams(previousLocked, _activeTradingDate.Value, utcNow);
                retainedStreams = retention.Retained;
                clearedStreams = retention.Removed;
                timetableDateRolledForward = true;
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "TRADING_DATE_ROLLED_FORWARD", state: "ENGINE",
                    new
                    {
                        previous_trading_date = previousLocked.ToString("yyyy-MM-dd"),
                        new_trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                        streams_cleared = clearedStreams,
                        streams_retained = retainedStreams,
                        playback_scenario = _playbackScenarioActive,
                        timetable_path = _timetablePath,
                        note = "Timetable advanced to a later session day - retaining nonterminal stream lifecycles and allowing eligible new-day stream ids to be created."
                    }));
            }
            else
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_TRADING_DATE_MISMATCH", state: "ENGINE",
                    new
                    {
                        locked_trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                        timetable_trading_date = timetableTradingDate.Value.ToString("yyyy-MM-dd"),
                        note = "Timetable trading_date is earlier than locked date - keeping existing lock (no backward roll)"
                    }));
            }
        }

        if (timetable.timezone != "America/Chicago")
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new
                {
                    reason = "TIMEZONE_MISMATCH",
                    expected_timezone = "America/Chicago",
                    actual_timezone = timetable.timezone,
                    trading_date = TradingDateString,
                    timetable_path = _timetablePath,
                    note = "Timetable timezone mismatch - engine will stand down"
                }));
            StandDown();
            return;
        }

        TryMaterializeInternalCalendarPolicy(utcNow);

        // Execution arming set from published timetable only (matrix-derived at publish; no separate eligibility file).
        var sessionTradingDate = _activeTradingDate!.Value;
        var publisherHash = string.IsNullOrWhiteSpace(timetable.timetable_hash)
            ? null
            : timetable.timetable_hash.Trim();
        _currentTimetableHash = publisherHash ?? poll.Hash;
        _currentTimetableVersionTimestamp = string.IsNullOrWhiteSpace(timetable.version_timestamp)
            ? null
            : timetable.version_timestamp.Trim();
        _heartbeatTradingDateCache = sessionTradingDate.ToString("yyyy-MM-dd");
        var eligibleSetBuilder = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in timetable.streams)
        {
            if (row.enabled && !string.IsNullOrWhiteSpace(row.stream))
                eligibleSetBuilder.Add(row.stream.Trim());
        }
        _eligibleSet = eligibleSetBuilder;
        var eligibleCount = _eligibleSet.Count;
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: sessionTradingDate.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_ENABLED_STREAM_SET", state: "ENGINE",
            new { enabled_stream_count = eligibleCount, note = "Streams with timetable.enabled derived from matrix at publish" }));

        var isReplayTimetable = timetable.metadata?.replay == true;

        // Only log timetable events when actually changed (or forced/initial load)
        // This reduces log verbosity when timetable hasn't changed
        if (force || timetableActuallyChanged || previousHash == null)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_UPDATED", state: "ENGINE",
                new
                {
                    previous_hash = previousHash,
                    new_hash = _lastTimetableHash,
                    enabled_stream_count = timetable.streams.Count(s => s.enabled),
                    total_stream_count = timetable.streams.Count,
                    trading_date = timetableTradingDate.Value.ToString("yyyy-MM-dd"),
                    date_locked = dateWasLocked,
                    note = "Timetable structure validated - trading date locked from timetable"
                }));

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: sessionTradingDate.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_LOADED", state: "ENGINE",
                new
                {
                    trading_date = sessionTradingDate.ToString("yyyy-MM-dd"),
                    timetable_hash = _currentTimetableHash,
                    version_timestamp = _currentTimetableVersionTimestamp
                }));

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_VALIDATED", state: "ENGINE",
                new { is_replay = isReplayTimetable, trading_date = timetableTradingDate.Value.ToString("yyyy-MM-dd"), note = "Trading date locked from timetable" }));
        }

        // Store timetable for later application
        _lastTimetable = timetable;

        // CRITICAL FIX: If trading date is locked but streams don't exist yet, create them now
        // This ensures streams are created immediately after timetable is loaded and trading date is locked
        if (_activeTradingDate.HasValue && _streams.Count == 0)
        {
            // CRITICAL FIX: Log before calling to ensure we can trace execution
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "STREAMS_CREATION_ATTEMPT", state: "ENGINE",
                new
                {
                    trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                    streams_count = _streams.Count,
                    spec_is_null = _spec is null,
                    time_is_null = _time is null,
                    last_timetable_is_null = _lastTimetable is null,
                    note = "Attempting to create streams after timetable loaded"
                }));
            EnsureStreamsCreated(utcNow);
        }
        else if (_activeTradingDate.HasValue && _streams.Count > 0)
        {
            // Apply timetable updates (e.g. NG 09:30 -> 11:00 slot change) but block adding NEW streams.
            // Matrix app auto-update can overwrite timetable mid-day; we allow slot-time updates for existing
            // streams but prevent new stream creation. See docs/robot/incidents/2026-03-04_TIMETABLE_OVERRIDE_INVESTIGATION.md
            ApplyTimetable(timetable, utcNow, allowNewStreams: timetableDateRolledForward, deferNonTerminalExistingStreams: timetableDateRolledForward);
        }
        else
        {
            // CRITICAL FIX: Log why stream creation is not being attempted
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "STREAMS_CREATION_NOT_ATTEMPTED", state: "ENGINE",
                new
                {
                    trading_date_has_value = _activeTradingDate.HasValue,
                    trading_date = _activeTradingDate.HasValue ? _activeTradingDate.Value.ToString("yyyy-MM-dd") : null,
                    streams_count = _streams.Count,
                    note = "Stream creation not attempted - check conditions"
                }));
        }
        // Otherwise, timetable will be applied when EnsureStreamsCreated() is called after trading date is locked

        // Health monitor no longer uses timetable for monitoring windows
        // (simplified to only monitor connection loss and data loss)
    }

    private void ApplyTimetable(TimetableContract timetable, DateTimeOffset utcNow, bool allowNewStreams = true, bool deferNonTerminalExistingStreams = false)
    {
        // Trading date must be locked before streams can be created
        if (_spec is null || _time is null || _lastTimetableHash is null || !_activeTradingDate.HasValue)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_APPLY_SKIPPED", state: "ENGINE",
                new { reason = "Missing spec, time, hash, or trading date" }));
            return;
        }

        // Validate timetable trading_date matches locked trading date
        if (!string.IsNullOrWhiteSpace(timetable.trading_date))
        {
            try
            {
                var timetableTradingDate = DateOnly.Parse(timetable.trading_date);
                if (_activeTradingDate.Value != timetableTradingDate)
                {
                    // Timetable trading_date differs from session lock (e.g. stale file); session/eligibility use lock — see ReloadTimetableIfChanged.
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_TRADING_DATE_MISMATCH", state: "ENGINE",
                        new
                        {
                            locked_trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                            timetable_trading_date = timetableTradingDate.ToString("yyyy-MM-dd"),
                            note = "Timetable trading_date differs from locked date - applying timetable using session lock"
                        }));
                }
            }
            catch (Exception ex)
            {
                // Invalid format - log but continue (trading date already locked)
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_INVALID_TRADING_DATE", state: "ENGINE",
                    new { trading_date = timetable.trading_date, error = ex.Message, note = "Failed to parse timetable trading_date - using locked date" }));
            }
        }

        var tradingDate = _activeTradingDate.Value; // Use locked trading date

        if (timetable.streams != null)
        {
            var deriveTradingDateStr = tradingDate.ToString("yyyy-MM-dd");
            foreach (var d in timetable.streams)
            {
                if (string.IsNullOrWhiteSpace(d.instrument))
                {
                    d.instrument = TimetableStream.DeriveInstrumentFromStreamId(d.stream ?? "");
                }

                if (string.IsNullOrWhiteSpace(d.session))
                {
                    d.session = TimetableStream.DeriveSessionFromStreamId(d.stream ?? "");
                }

                if (string.IsNullOrWhiteSpace(d.decision_time))
                {
                    d.decision_time = d.slot_time ?? "";
                }

                if (_loggingConfig.DiagnosticsEnabled)
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: deriveTradingDateStr, eventType: "TIMETABLE_DERIVED", state: "ENGINE",
                        new
                        {
                            stream = d.stream,
                            instrument = d.instrument,
                            canonical = GetCanonicalInstrument(d.instrument ?? ""),
                            session = d.session,
                            slot_time = d.slot_time,
                            enabled = d.enabled
                        }));
                }
            }
        }

        var incoming = timetable.streams.Where(s => s.enabled).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var streamIdOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // PHASE 4: Validate execution policy for unique execution instruments before stream creation (fail-closed)
        // Validation logic lives in GetOrderQuantity() - do not duplicate policy rules here
        // For each enabled directive:
        //   1. Determine canonical market (timetable's instrument field is canonical)
        //   2. Determine actual execution instrument (from _executionInstrument anchor or policy)
        //   3. Call GetOrderQuantity(canonical, execution) - this validates policy
        //   4. Aggregate exceptions and fail-closed once
        var uniqueCanonicalInstruments = incoming
            .Select(d => (d.instrument ?? "").Trim())
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var quantityValidationErrors = new List<string>();
        var uniqueExecutionInstruments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var timetableInstrument in uniqueCanonicalInstruments)
        {
            try
            {
                // Timetable's instrument field is canonical (ES, NQ, YM, etc.)
                var canonicalInst = GetCanonicalInstrument(timetableInstrument);

                // Determine actual execution instrument that will be used:
                // 1. If _executionInstrument is set and matches canonical market, use it
                // 2. Otherwise, get the enabled execution instrument from policy
                string executionInst;
                if (!string.IsNullOrWhiteSpace(_executionInstrument))
                {
                    var ntCanonical = GetCanonicalInstrument(_executionInstrument.ToUpperInvariant());
                    if (string.Equals(ntCanonical, canonicalInst, StringComparison.OrdinalIgnoreCase))
                    {
                        // Use NinjaTrader's execution instrument (anchor)
                        executionInst = _executionInstrument.ToUpperInvariant();
                    }
                    else
                    {
                        // Different canonical market - get enabled instrument from policy
                        executionInst = _executionPolicy?.GetEnabledExecutionInstrument(canonicalInst) ?? timetableInstrument;
                    }
                }
                else
                {
                    // No anchor - get enabled instrument from policy
                    executionInst = _executionPolicy?.GetEnabledExecutionInstrument(canonicalInst) ?? timetableInstrument;
                }

                if (string.IsNullOrWhiteSpace(executionInst))
                {
                    quantityValidationErrors.Add($"Execution instrument for canonical market '{canonicalInst}': No enabled execution instrument found in policy.");
                    continue;
                }

                // Track unique execution instruments for logging
                uniqueExecutionInstruments.Add(executionInst);

                // PHASE 4: Call GetOrderQuantity - this validates policy (canonical market exists, instrument enabled, quantity valid)
                // Do not manually re-encode policy rules here - validation logic must live in exactly one place
                var qty = GetOrderQuantity(canonicalInst, executionInst);
                // GetOrderQuantity already validates qty > 0, so no double-check needed
            }
            catch (ArgumentException ex)
            {
                quantityValidationErrors.Add($"Canonical market '{timetableInstrument}': {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                quantityValidationErrors.Add($"Canonical market '{timetableInstrument}': {ex.Message}");
            }
        }

        if (quantityValidationErrors.Count > 0)
        {
            var errorMsg = $"PHASE 4: Execution policy validation failed:\n{string.Join("\n", quantityValidationErrors)}";
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                eventType: "EXECUTION_POLICY_VALIDATION_FAILED", state: "ENGINE",
                new
                {
                    errors = quantityValidationErrors,
                    unique_execution_instruments = uniqueExecutionInstruments.ToList(),
                    note = "Execution blocked due to execution policy validation failures"
                }));

            // Centralized notification (single call for all quantity validation errors)
            ReportExecutionPolicyFailure(
                summary: "Execution policy quantity validation failed",
                details: quantityValidationErrors,
                context: new Dictionary<string, object>
                {
                    ["unique_execution_instruments"] = uniqueExecutionInstruments.ToList()
                }
            );

            throw new InvalidOperationException(errorMsg);
        }

        // Log timetable parsing stats
        int acceptedCount = 0;
        int skippedCount = 0;
        int committedDirectiveSkips = 0;
        int blockedNewStreamMidSession = 0;
        var skippedReasons = new Dictionary<string, int>();

        foreach (var directive in incoming)
        {
            var timetableInstrument = (directive.instrument ?? "").ToUpperInvariant();
            var canonicalInstrument = GetCanonicalInstrument(timetableInstrument);
            var session = directive.session ?? "";
            var slotTimeChicago = directive.slot_time ?? "";

            // 🔒 INVARIANT: The strategy's enabled instrument in NinjaTrader is the primary execution anchor.
            // Policy may only restrict or remap execution after anchoring to that instrument.
            // - Anchor: Use _executionInstrument if MasterInstrument.Name matches canonical market
            // - Restrict: Policy validation in GetOrderQuantity() will fail if instrument is disabled
            // - Remap: Not supported (NinjaTrader strategies are single-instrument)
            //
            // CRITICAL RULE: If a strategy is enabled on a specific contract in NinjaTrader, that contract MUST be traded,
            // provided its MasterInstrument matches a timetable canonical instrument.
            // This check happens FIRST before any processing to avoid unnecessary work
            if (!string.IsNullOrWhiteSpace(_executionInstrument))
            {
                if (string.IsNullOrWhiteSpace(canonicalInstrument))
                {
                    skippedCount++;
                    var missReason = "MISSING_CANONICAL";
                    if (!skippedReasons.ContainsKey(missReason))
                        skippedReasons[missReason] = 0;
                    skippedReasons[missReason]++;
                    var missTradingDateStr = tradingDate.ToString("yyyy-MM-dd");
                    DateTimeOffset? missSlotUtc = null;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(slotTimeChicago))
                            missSlotUtc = _time.ConvertChicagoToUtc(_time.ConstructChicagoTime(tradingDate, slotTimeChicago));
                    }
                    catch { }
                    LogEvent(RobotEvents.Base(_time, utcNow, missTradingDateStr, directive.stream ?? "", canonicalInstrument, session, slotTimeChicago, missSlotUtc,
                        "STREAM_SKIPPED", "ENGINE", new
                        {
                            reason = "MISSING_CANONICAL",
                            stream_id = directive.stream,
                            timetable_instrument_raw = timetableInstrument,
                            note = "Timetable canonical is empty after derivation — cannot match NinjaTrader anchor"
                        }));
                    continue;
                }

                // Use MasterInstrument.Name for explicit canonical matching (as per authoritative rule)
                // CRITICAL FIX: Must canonicalize MasterInstrument.Name (e.g., M2K -> RTY, MGC -> GC) before comparison
                string? ntCanonical = null;
                if (!string.IsNullOrWhiteSpace(_masterInstrumentName))
                {
                    ntCanonical = GetCanonicalInstrument(_masterInstrumentName);
                }
                else
                {
                    ntCanonical = GetCanonicalInstrument(_executionInstrument);
                }

                if (!string.Equals(ntCanonical, canonicalInstrument, StringComparison.OrdinalIgnoreCase))
                {
                    // Skip this directive - MasterInstrument does not match timetable canonical instrument
                    skippedCount++;
                    var skipReason = "CANONICAL_MISMATCH";
                    if (!skippedReasons.ContainsKey(skipReason))
                        skippedReasons[skipReason] = 0;
                    skippedReasons[skipReason]++;

                    // FAIL LOUDLY: Log per-stream skip with detailed context
                    var skipTradingDateStr = tradingDate.ToString("yyyy-MM-dd");
                    var skipSlotTimeUtc = _time.ConvertChicagoToUtc(_time.ConstructChicagoTime(tradingDate, slotTimeChicago));
                    LogEvent(RobotEvents.Base(_time, utcNow, skipTradingDateStr, directive.stream ?? "", canonicalInstrument, session, slotTimeChicago, skipSlotTimeUtc,
                        "STREAM_SKIPPED", "ENGINE", new
                        {
                            reason = "CANONICAL_MISMATCH",
                            stream_id = directive.stream,
                            timetable_canonical = canonicalInstrument,
                            ninjatrader_master_instrument = ntCanonical ?? "NULL",
                            ninjatrader_execution_instrument = _executionInstrument ?? "NULL",
                            session = session,
                            slot_time = slotTimeChicago,
                            note = "MasterInstrument does not match timetable canonical instrument - directive skipped per authoritative rule"
                        }));

                    continue; // Skip to next directive
                }
            }

            // If we reach here, canonical matches - proceed with anchoring
            // CRITICAL: Use NinjaTrader instrument if it matches timetable instrument or its canonical equivalent
            // This handles: NG (timetable) + MNG (NinjaTrader) -> use MNG
            // And: NG (timetable) + NG (NinjaTrader) -> use NG
            // This allows the robot to use whatever instrument is actually enabled in NinjaTrader
            var executionInstrument = timetableInstrument;
            if (!string.IsNullOrWhiteSpace(_executionInstrument))
            {
                var ntInstrument = _executionInstrument.ToUpperInvariant();
                var ntCanonical = GetCanonicalInstrument(ntInstrument); // Already computed above, but keeping for clarity

                // Canonical matches (we wouldn't be here otherwise), so use NinjaTrader instrument
                executionInstrument = ntInstrument;
            }

            // Policy validation happens later in GetOrderQuantity() - if instrument is disabled, it will fail there
            // This respects the anchor invariant: policy can only RESTRICT, not override the anchor

            // PHASE 2: Canonicalize stream ID - must use canonical instrument, not execution
            var streamId = directive.stream;
            if (!string.IsNullOrWhiteSpace(streamId) && !string.IsNullOrWhiteSpace(executionInstrument))
            {
                // Map execution instrument in stream ID to canonical instrument
                // e.g., "MES1" -> "ES1"
                if (streamId.IndexOf(executionInstrument, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Replace doesn't support StringComparison, so use manual replacement
                    var index = streamId.IndexOf(executionInstrument, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0)
                    {
                        streamId = streamId.Substring(0, index) + canonicalInstrument + streamId.Substring(index + executionInstrument.Length);
                    }
                }
            }

            // Timetable-enabled set (timetable.enabled==true): only arm streams present in loaded file's enabled set
            if (_eligibleSet != null && !string.IsNullOrWhiteSpace(streamId) && !_eligibleSet.Contains(streamId))
            {
                skippedCount++;
                var skipReason = "NOT_ELIGIBLE";
                if (!skippedReasons.ContainsKey(skipReason))
                    skippedReasons[skipReason] = 0;
                skippedReasons[skipReason]++;
                var skipTradingDateStr = tradingDate.ToString("yyyy-MM-dd");
                DateTimeOffset? skipSlotTimeUtc = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(slotTimeChicago))
                        skipSlotTimeUtc = _time.ConvertChicagoToUtc(_time.ConstructChicagoTime(tradingDate, slotTimeChicago));
                }
                catch { }
                LogEvent(RobotEvents.Base(_time, utcNow, skipTradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, skipSlotTimeUtc,
                    "DIRECTIVE_IGNORED_NOT_ELIGIBLE", "ENGINE", new
                    {
                        stream_key = streamId,
                        slot_time = slotTimeChicago,
                        note = "Stream not in timetable.enabled set for session - directive ignored"
                    }));
                continue;
            }

            // Track stream ID occurrences for duplicate detection (using canonical stream ID)
            if (!string.IsNullOrWhiteSpace(streamId))
            {
                if (!streamIdOccurrences.TryGetValue(streamId, out var count))
                    count = 0;
                streamIdOccurrences[streamId] = count + 1;
            }
            DateTimeOffset? slotTimeUtc = null;
            try
            {
                // CRITICAL FIX: Use tradingDate parameter instead of _activeTradingDate
                if (!string.IsNullOrWhiteSpace(slotTimeChicago))
                {
                    var slotTimeChicagoTime = _time.ConstructChicagoTime(tradingDate, slotTimeChicago);
                    slotTimeUtc = _time.ConvertChicagoToUtc(slotTimeChicagoTime);
                }
            }
            catch
            {
                slotTimeUtc = null;
            }

            var tradingDateStr = tradingDate.ToString("yyyy-MM-dd"); // Use parameter, not _activeTradingDate

            // Log execution instrument override if it occurred (after slotTimeUtc is computed)
            if (!string.IsNullOrWhiteSpace(_executionInstrument) &&
                !string.Equals(executionInstrument, timetableInstrument, StringComparison.OrdinalIgnoreCase))
            {
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, directive.stream ?? "", canonicalInstrument, session, slotTimeChicago, slotTimeUtc ?? DateTimeOffset.UtcNow,
                    "EXECUTION_INSTRUMENT_OVERRIDE", "ENGINE",
                    new
                    {
                        timetable_instrument = timetableInstrument,
                        ninjatrader_instrument = _executionInstrument.ToUpperInvariant(),
                        execution_instrument = executionInstrument,
                        canonical_instrument = canonicalInstrument,
                        note = "Using NinjaTrader instrument for execution (matches timetable canonical)"
                    }));
            }

            if (string.IsNullOrWhiteSpace(streamId) ||
                string.IsNullOrWhiteSpace(executionInstrument) ||
                string.IsNullOrWhiteSpace(session) ||
                string.IsNullOrWhiteSpace(slotTimeChicago))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("MISSING_FIELDS", out var count)) count = 0;
                skippedReasons["MISSING_FIELDS"] = count + 1;
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId ?? "", canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new
                    {
                        reason = "MISSING_FIELDS",
                        stream_id = streamId,
                        execution_instrument = executionInstrument,
                        canonical_instrument = canonicalInstrument,
                        session = session,
                        slot_time = slotTimeChicago
                    }));
                continue;
            }

            if (!_spec.sessions.ContainsKey(session))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("UNKNOWN_SESSION", out var count2)) count2 = 0;
                skippedReasons["UNKNOWN_SESSION"] = count2 + 1;
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new
                    {
                        reason = "UNKNOWN_SESSION",
                        stream_id = streamId,
                        execution_instrument = executionInstrument,
                        canonical_instrument = canonicalInstrument,
                        session = session
                    }));
                continue;
            }

            // slot_time validation (fail closed per stream)
            var allowed = _spec.sessions[session].slot_end_times;
            if (!allowed.Contains(slotTimeChicago))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("INVALID_SLOT_TIME", out var count3)) count3 = 0;
                skippedReasons["INVALID_SLOT_TIME"] = count3 + 1;
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new
                    {
                        reason = "INVALID_SLOT_TIME",
                        stream_id = streamId,
                        execution_instrument = executionInstrument,
                        canonical_instrument = canonicalInstrument,
                        slot_time = slotTimeChicago,
                        allowed_times = allowed
                    }));
                continue;
            }

            // PHASE 2: Timetable validation uses canonical instrument (logic identity)
            if (!_spec.TryGetInstrument(canonicalInstrument, out _))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("UNKNOWN_INSTRUMENT", out var count4)) count4 = 0;
                skippedReasons["UNKNOWN_INSTRUMENT"] = count4 + 1;
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new
                    {
                        reason = "UNKNOWN_INSTRUMENT",
                        stream_id = streamId,
                        execution_instrument = executionInstrument,
                        canonical_instrument = canonicalInstrument
                    }));
                continue;
            }

            seen.Add(streamId);
            acceptedCount++;

            if (_streams.TryGetValue(streamId, out var sm))
            {
                if (sm.Committed)
                {
                    committedDirectiveSkips++;
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                        "UPDATE_IGNORED_COMMITTED", "ENGINE", new
                        {
                            reason = "STREAM_COMMITTED",
                            execution_instrument = executionInstrument,
                            canonical_instrument = canonicalInstrument
                        }));
                    continue;
                }

                if (deferNonTerminalExistingStreams && sm.HasTimetableRolloverRetentionEvidence)
                {
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                        "TIMETABLE_STREAM_DEFERRED_PRIOR_LIFECYCLE_ACTIVE", "ENGINE", new
                        {
                            reason = "PRIOR_LIFECYCLE_ACTIVE",
                            stream_id = streamId,
                            new_timetable_trading_date = tradingDateStr,
                            retained_lifecycle_trading_date = sm.TradingDate,
                            retained_state = sm.State.ToString(),
                            retained_committed = sm.Committed,
                            retained_slot_status = sm.SlotStatus.ToString(),
                            retained_execution_interrupted_by_close = sm.ExecutionInterruptedByClose,
                            note = "New-day row for this stream id was deferred because one matrix lane supports one active lifecycle at a time."
                        }));
                    continue;
                }

                // Apply updates only for uncommitted streams
                if (string.IsNullOrWhiteSpace(directive.slot_time))
                {
                    // Fail closed: skip update if slot_time is null/empty
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, sm.SlotTimeChicago, null,
                        "STREAM_UPDATE_SKIPPED", "ENGINE", new
                        {
                            reason = "EMPTY_SLOT_TIME",
                            previous_slot_time = sm.SlotTimeChicago,
                            note = "Update skipped due to empty slot_time in timetable"
                        }));
                    continue;
                }

                var oldSlot = sm.SlotTimeChicago;
                var applied = sm.ApplyDirectiveUpdate(directive.slot_time, _activeTradingDate.Value, utcNow);
                if (applied)
                {
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                        "DIRECTIVE_UPDATE_APPLIED", "ENGINE", new { old_slot = oldSlot, new_slot = directive.slot_time }));
                }
                else
                {
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                        "DIRECTIVE_IGNORED_STATE_LOCKED", "ENGINE", new { stream_key = streamId, state = sm.State.ToString() }));
                }
            }
            else if (!allowNewStreams)
            {
                // Mid-day update: block new stream creation (e.g. matrix auto-update added NQ2, GC2).
                // Slot-time changes for existing streams are applied above. Only block additions.
                // Mid-day update path: no new streams when allowNewStreams is false (policy gate).
                blockedNewStreamMidSession++;
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_ADDITION_BLOCKED", "ENGINE", new
                    {
                        reason = "SESSION_FREEZE",
                        stream_id = streamId,
                        slot_time = slotTimeChicago,
                        note = "New stream blocked during mid-day timetable update; slot-time changes for existing streams may still apply"
                    }));
            }
            else
            {
                // PHASE 2: Fail-fast assertion - ensure stream ID is canonicalized
                if (executionInstrument != canonicalInstrument && streamId.IndexOf(executionInstrument, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new InvalidOperationException(
                        $"PHASE 2 ASSERTION FAILED: Execution instrument '{executionInstrument}' leaked into logic stream ID '{streamId}'. " +
                        $"Expected canonical stream ID using '{canonicalInstrument}'. " +
                        $"This indicates incomplete canonicalization."
                    );
                }

                // PHASE 3: New stream - pass DateOnly directly (authoritative), convert to string only for logging/journal

                // PHASE 3: Fail-fast assertion - ensure stream ID is canonicalized before stream creation
                if (executionInstrument != canonicalInstrument && streamId.IndexOf(executionInstrument, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new InvalidOperationException(
                        $"PHASE 3 ASSERTION FAILED: Execution instrument '{executionInstrument}' leaked into logic stream ID '{streamId}'. " +
                        $"Expected canonical stream ID using '{canonicalInstrument}'. " +
                        $"This indicates incomplete canonicalization in ApplyTimetable."
                    );
                }

                // PHASE 3: Assert canonical stream ID matches canonical instrument
                if (!streamId.StartsWith(canonicalInstrument, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"PHASE 3 ASSERTION FAILED: Stream ID '{streamId}' does not start with canonical instrument '{canonicalInstrument}'. " +
                        $"Stream IDs must use canonical instrument prefix (e.g., ES1, not MES1)."
                    );
                }

                // PHASE 4: Resolve order quantity from policy (canonical + execution)
                // Use canonical+execution overload to avoid GetCanonicalInstrument divergence risk
                // Add assertion that derived canonical matches directive's canonical identity
                var derivedCanonical = GetCanonicalInstrument(executionInstrument);
                if (derivedCanonical != canonicalInstrument)
                {
                    throw new InvalidOperationException(
                        $"PHASE 4 ASSERTION FAILED: GetCanonicalInstrument('{executionInstrument}') returned '{derivedCanonical}' " +
                        $"but directive canonical is '{canonicalInstrument}'. Canonical identity divergence detected.");
                }

                var orderQuantity = GetOrderQuantity(canonicalInstrument, executionInstrument);
                var (baseSize, maxSize) = GetOrderQuantityInfo(canonicalInstrument, executionInstrument);

                // CRITICAL FIX: Create modified directive with execution instrument instead of canonical
                // The timetable directive.instrument is canonical (GC), but StreamStateMachine needs execution instrument (MGC)
                // Create a copy of the directive with the correct execution instrument
                var modifiedDirective = new TimetableStream
                {
                    stream = directive.stream,
                    instrument = executionInstrument, // Use execution instrument (MGC), not canonical (GC)
                    session = directive.session,
                    slot_time = directive.slot_time,
                    enabled = directive.enabled,
                    block_reason = directive.block_reason,
                    decision_time = directive.decision_time
                };

                // PHASE 3: Pass DateOnly to constructor (will be converted to string internally for journal)
                // PHASE 4: Pass order quantity (policy-controlled, Chart Trader ignored)
                // Use named arguments after first optional parameter to avoid future breakage
                var newSm = new StreamStateMachine(
                    _time,
                    _spec,
                    _log,
                    _journals,
                    tradingDate,
                    _lastTimetableHash,
                    modifiedDirective, // Use modified directive with execution instrument
                    _executionMode,
                    orderQuantity, // baseSize (existing parameter)
                    maxSize, // NEW: maxSize parameter
                    _root, // Repository root (market data)
                    _persistenceBase, // Robot state root (journals + hydration JSONL; isolated when SIM + ignoreExistingStreamJournals)
                    ignoreExistingStreamJournals: _ignoreExistingStreamJournals,
                    executionAdapter: _executionAdapter,
                    riskGate: _riskGate,
                    executionJournal: _executionJournal,
                    loggingConfig: _loggingConfig,
                    engine: this, // Pass engine reference for BarsRequest status checks
                    isInstrumentBlockedForReentry: inst => IsInstrumentBlockedForReentry(inst),
                    emergencyFlatten: (inst, utcNow) =>
                    {
                        if (_executionAdapter?.TryEnqueueEmergencyFlattenProtective(inst, utcNow) == true)
                            return FlattenResult.FailureResult("Enqueued for strategy thread", utcNow);
                        return FlattenResult.FailureResult("Emergency flatten enqueue not supported for this adapter", utcNow);
                    },
                    isIeaQueueHealthyForInstrument: inst => IsIeaQueueHealthyForInstrument(inst),
                    eventWriter: _eventWriter,
                    streamInitializationEventUtc: utcNow);

                // PHASE 3: Post-creation assertion - verify stream properties match expectations
                if (newSm.ExecutionInstrument != executionInstrument)
                {
                    throw new InvalidOperationException(
                        $"PHASE 3 ASSERTION FAILED: Stream ExecutionInstrument '{newSm.ExecutionInstrument}' does not match expected '{executionInstrument}'."
                    );
                }
                if (newSm.CanonicalInstrument != canonicalInstrument)
                {
                    throw new InvalidOperationException(
                        $"PHASE 3 ASSERTION FAILED: Stream CanonicalInstrument '{newSm.CanonicalInstrument}' does not match expected '{canonicalInstrument}'."
                    );
                }
                if (newSm.Stream != streamId)
                {
                    throw new InvalidOperationException(
                        $"PHASE 3 ASSERTION FAILED: Stream ID '{newSm.Stream}' does not match canonicalized stream ID '{streamId}'."
                    );
                }
                if (newSm.Instrument != canonicalInstrument)
                {
                    throw new InvalidOperationException(
                        $"PHASE 3 ASSERTION FAILED: Stream Instrument property '{newSm.Instrument}' is not canonical '{canonicalInstrument}'. " +
                        $"Instrument property must represent logic identity (canonical)."
                    );
                }

                // PHASE 2: Verify stream ID matches canonical instrument
                if (newSm.ExecutionInstrument != newSm.CanonicalInstrument &&
                    !string.Equals(newSm.Stream, streamId, StringComparison.OrdinalIgnoreCase))
                {
                    // Stream ID should already be canonicalized above, but double-check
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                        "STREAM_ID_CANONICALIZATION_WARNING", "ENGINE", new
                        {
                            stream_id_from_timetable = directive.stream,
                            canonicalized_stream_id = streamId,
                            execution_instrument = newSm.ExecutionInstrument,
                            canonical_instrument = newSm.CanonicalInstrument,
                            note = "Stream ID was canonicalized from timetable"
                        }));
                }

                // PHASE 2: Log stream creation with both instruments for observability
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_CREATED", "ENGINE", new
                    {
                        stream_id = streamId,
                        execution_instrument = newSm.ExecutionInstrument,
                        canonical_instrument = newSm.CanonicalInstrument,
                        session = session,
                        slot_time = slotTimeChicago
                    }));

                if (deferNonTerminalExistingStreams)
                {
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                        "TIMETABLE_ROLLOVER_NEW_STREAM_CREATED", "ENGINE", new
                        {
                            stream_id = streamId,
                            trading_date = tradingDateStr,
                            execution_instrument = newSm.ExecutionInstrument,
                            canonical_instrument = newSm.CanonicalInstrument,
                            session = session,
                            slot_time = slotTimeChicago,
                            note = "New-day stream created after timetable rollover because no nonterminal lifecycle used this stream id."
                        }));
                }

                // PHASE 4: Set alert callback for high-priority alerts only (RANGE_INVALIDATED)
                // Non-critical alerts (gaps, pre-hydration, state transitions) are logged only, not notified
                var notificationService = GetNotificationService();
                if (notificationService != null)
                {
                    newSm.SetAlertCallback((key, title, message, priority) =>
                    {
                        // Only notify for RANGE_INVALIDATED (handled in StreamStateMachine)
                        // All other alerts are logged only (no notification)
                        if (key.StartsWith("RANGE_INVALIDATED:", StringComparison.OrdinalIgnoreCase))
                        {
                            notificationService.EnqueueNotification(key, title, message, priority);
                        }
                        // All other alert callbacks are suppressed (log only)
                    });
                }

                // Set critical event reporting callback for EXECUTION_GATE_INVARIANT_VIOLATION
                if (_healthMonitor != null)
                {
                    newSm.SetReportCriticalCallback((eventType, payload, tradingDate) =>
                    {
                        // Audit clarity only: embed run_id + trading_date into payload
                        if (!string.IsNullOrWhiteSpace(_runId))
                            payload["run_id"] = _runId;
                        if (!string.IsNullOrWhiteSpace(tradingDate))
                            payload["trading_date"] = tradingDate;

                        _healthMonitor.ReportCritical(eventType, payload);
                    });
                }

                _streams[streamId] = newSm;

                if (newSm.Committed)
                {
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                        "STREAM_SKIPPED", "ENGINE", new
                        {
                            reason = "ALREADY_COMMITTED_JOURNAL",
                            execution_instrument = newSm.ExecutionInstrument,
                            canonical_instrument = newSm.CanonicalInstrument
                        }));
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
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDate.ToString("yyyy-MM-dd"), eventType: "DUPLICATE_STREAM_ID", state: "ENGINE",
                    new
                    {
                        stream_id = kvp.Key,
                        occurrence_count = kvp.Value,
                        note = "Duplicate stream ID detected in timetable - last occurrence will be used"
                    }));
            }
        }

        // Log parsing summary
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDate.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_PARSING_COMPLETE", state: "ENGINE",
            new {
                total_enabled = incoming.Count,
                accepted = acceptedCount,
                skipped = skippedCount,
                skipped_reasons = skippedReasons,
                streams_armed = _streams.Count
            }));

        if (skippedCount > 0 || committedDirectiveSkips > 0 || blockedNewStreamMidSession > 0)
        {
            var expectedAnchorFilter =
                !string.IsNullOrWhiteSpace(_executionInstrument)
                && skippedCount > 0
                && committedDirectiveSkips == 0
                && blockedNewStreamMidSession == 0
                && skippedReasons.Count == 1
                && skippedReasons.TryGetValue("CANONICAL_MISMATCH", out var anchorFilteredCount)
                && anchorFilteredCount == skippedCount;
            var eventType = expectedAnchorFilter
                ? "TIMETABLE_APPLY_ANCHOR_FILTERED"
                : "TIMETABLE_APPLY_PARTIAL_REFUSAL";
            var note = expectedAnchorFilter
                ? "Timetable application skipped other canonical instruments because this NinjaTrader strategy instance is anchored to one execution instrument; per-stream skips are expected audit evidence."
                : "Timetable had enabled directives that were skipped, blocked, or not applied; execution not fully armed for every enabled row.";

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDate.ToString("yyyy-MM-dd"),
                eventType: eventType, state: "ENGINE",
                new
                {
                    timetable_enabled_directives = incoming.Count,
                    accepted = acceptedCount,
                    skipped = skippedCount,
                    skipped_reasons = skippedReasons,
                    committed_directive_skips = committedDirectiveSkips,
                    blocked_mid_session_new_streams = blockedNewStreamMidSession,
                    expected_anchor_filter = expectedAnchorFilter,
                    ninjatrader_execution_instrument = _executionInstrument ?? "",
                    classification = expectedAnchorFilter ? "AUDIT_EXPECTED_ANCHOR_FILTER" : "PARTIAL_REFUSAL",
                    note = note
                }));
        }

        // Any existing streams not present in timetable are left as-is; timetable is authoritative about enabled streams,
        // but the skeleton remains fail-closed (no orders) regardless.
    }

    /// <summary>Parity with Python <c>modules.timetable.cme_session.get_cme_trading_date</c>: UTC → America/Chicago; hour ≥ 18 → next calendar day; else Chicago calendar day.</summary>
    private static string ComputeCmeTradingDateString(DateTimeOffset utcNow)
    {
        var tz = GetChicagoTimeZone();
        var utcDt = DateTime.SpecifyKind(utcNow.UtcDateTime, DateTimeKind.Utc);
        var chicago = TimeZoneInfo.ConvertTimeFromUtc(utcDt, tz);
        var date = chicago.Date;
        if (chicago.Hour >= 18)
            date = date.AddDays(1);
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>True when Chicago wall time is before the 18:00 CT boundary used by <see cref="ComputeCmeTradingDateString"/>.</summary>
    private static bool IsChicagoWallHourStrictlyBeforeCmeRollover(DateTimeOffset utcNow)
    {
        var tz = GetChicagoTimeZone();
        var utcDt = DateTime.SpecifyKind(utcNow.UtcDateTime, DateTimeKind.Utc);
        var chicago = TimeZoneInfo.ConvertTimeFromUtc(utcDt, tz);
        return chicago.Hour < 18;
    }

    private static TimeZoneInfo GetChicagoTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        }
    }

    private static string? ComputeFileHash(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            var hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant().Substring(0, 16);
        }
        catch { return null; }
    }

    private void StandDown()
    {
        Volatile.Write(ref _shutdownRequested, 1);
        StopEngineHeartbeatTimer();
        var utcNow = DateTimeOffset.UtcNow;
        var streamCount = _streams.Count;
        var tradingDateStr = TradingDateString;

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "ENGINE_STAND_DOWN", state: "ENGINE",
            new
            {
                reason = "Timetable validation failure",
                trading_date = tradingDateStr,
                timetable_path = _timetablePath,
                streams_cleared = streamCount,
                active_trading_date_cleared = _activeTradingDate.HasValue,
                note = "Engine stand-down due to timetable validation failure - all streams cleared, trading date reset"
            }));

        _streams.Clear();
        _activeTradingDate = null;
        _currentTimetableHash = null;
        _currentTimetableVersionTimestamp = null;
    }

    /// <summary>
    /// Get timetable file path (for UI/logging so strategy can show "timetable read from X").
    /// </summary>
    public string GetTimetablePath()
    {
        lock (_engineLock)
        {
            return _timetablePath ?? "";
        }
    }

    private static string ResolveDefaultTimetablePath(string projectRoot, string? customTimetablePath, bool playbackAccountDetected)
    {
        if (!string.IsNullOrWhiteSpace(customTimetablePath))
            return customTimetablePath;

        var timetableDir = Path.Combine(projectRoot, "data", "timetable");
        if (playbackAccountDetected)
        {
            var replayCurrent = Path.Combine(timetableDir, "timetable_replay_current.json");
            if (File.Exists(replayCurrent))
                return replayCurrent;

            var replayTemplate = Path.Combine(timetableDir, "timetable_replay.json");
            if (File.Exists(replayTemplate))
                return replayTemplate;
        }

        return Path.Combine(timetableDir, "timetable_current.json");
    }

}
