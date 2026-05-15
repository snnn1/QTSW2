using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QTSW2.Robot.Core.Execution;
using QTSW2.Robot.Core.SessionAuthority;

namespace QTSW2.Robot.Core;

public sealed partial class RobotEngine
{
    /// <summary>
    /// Set session start time for an instrument (from TradingHours).
    /// Called by NinjaTrader strategy to provide instrument-specific session start time.
    /// </summary>
    /// <param name="instrument">Instrument name (e.g., "ES")</param>
    /// <param name="sessionStartTime">Session start time in HH:MM format (e.g., "17:00")</param>
    public void SetSessionStartTime(string instrument, string sessionStartTime)
    {
        lock (_engineLock)
        {
            if (string.IsNullOrWhiteSpace(instrument) || string.IsNullOrWhiteSpace(sessionStartTime))
                return;

            var instrumentUpper = instrument.ToUpperInvariant();
            _sessionStartTimes[instrumentUpper] = sessionStartTime;

            LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: TradingDateString, eventType: "SESSION_START_TIME_SET", state: "ENGINE",
                new
                {
                    instrument = instrumentUpper,
                    session_start_time = sessionStartTime,
                    source = "TradingHours",
                    note = "Session start time set from NinjaTrader TradingHours template"
                }));
        }
    }

    /// <summary>
    /// Set session close resolution result for (tradingDay, sessionClass).
    /// Called by strategy (SessionCloseResolver) or HistoricalReplay harness.
    /// Emits SESSION_CLOSE_RESOLVED or failure event per FailureReason:
    /// SESSION_CLOSE_HOLIDAY, SESSION_CLOSE_NO_ELIGIBLE_SEGMENTS, SESSION_CLOSE_ITERATION_ERROR, SESSION_CLOSE_EXCEPTION.
    /// </summary>
    public void SetSessionCloseResolved(string tradingDay, string sessionClass, SessionCloseResult result)
    {
        if (string.IsNullOrWhiteSpace(tradingDay) || string.IsNullOrWhiteSpace(sessionClass)) return;
        if (sessionClass != "S1" && sessionClass != "S2") return;

        var r = result ?? new SessionCloseResult();
        var utcNow = DateTimeOffset.UtcNow;
        if (!r.HasSession && string.Equals(r.FailureReason, "HOLIDAY", StringComparison.Ordinal))
        {
            var (hasConflict, sessionTotalStreams, sessionEnabledStreams, sessionCalendarBlockedStreams, timetableTradingDay, activeTradingDay) =
                DetectNtHolidayConflict(tradingDay, sessionClass);
            if (hasConflict)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDay, eventType: "SESSION_CLOSE_HOLIDAY_CONFLICT", state: "ENGINE",
                    new Dictionary<string, object?>
                    {
                        ["trading_day"] = tradingDay,
                        ["session_class"] = sessionClass,
                        ["failure_reason"] = r.FailureReason ?? "UNKNOWN",
                        ["timetable_trading_day"] = timetableTradingDay,
                        ["active_trading_day"] = activeTradingDay,
                        ["session_total_streams"] = sessionTotalStreams,
                        ["session_enabled_streams"] = sessionEnabledStreams,
                        ["session_calendar_blocked_streams"] = sessionCalendarBlockedStreams,
                        ["prefer_internal_calendar_over_nt_holiday"] = _loggingConfig.prefer_internal_calendar_over_nt_holiday,
                        ["note"] = "NinjaTrader TradingHours marked HOLIDAY while timetable indicates session activity"
                    }));
            }
        }

        bool shouldEmit;
        lock (_engineLock)
        {
            var key = (tradingDay, sessionClass);
            _sessionCloseResults[key] = r;
            shouldEmit = _sessionCloseEmittedKeys.Add(key);
        }

        if (!shouldEmit) return; // Already emitted for this (tradingDay, sessionClass)

        if (r.HasSession)
        {
            TryEmitSessionFlattenVisibility(tradingDay, sessionClass, "NT_CACHE", true,
                r.ResolvedSessionCloseUtc, r.FlattenTriggerUtc, r.BufferSeconds, utcNow);
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDay, eventType: "SESSION_CLOSE_RESOLVED", state: "ENGINE",
                new
                {
                    trading_day = tradingDay,
                    session_class = sessionClass,
                    instrument = r.BarsInstrument ?? _executionInstrument ?? "N/A",
                    flatten_trigger_utc = r.FlattenTriggerUtc?.ToString("o"),
                    resolved_session_close_utc = r.ResolvedSessionCloseUtc?.ToString("o"),
                    buffer_seconds = r.BufferSeconds,
                    resolution_source = "LIVE_RESOLVER"
                }));
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDay, eventType: "SESSION_CLOSE_RESOLUTION_SUCCESS", state: "ENGINE",
                new { trading_day = tradingDay, session_class = sessionClass, bars_count = r.BarsCount, bars_instrument = r.BarsInstrument ?? "N/A" }));
        }
        else
        {
            TryEmitSessionFlattenVisibility(tradingDay, sessionClass, "NT_CACHE", false,
                null, null, 0, utcNow, r.FailureReason);
            var eventType = r.FailureReason switch
            {
                "HOLIDAY" => "SESSION_CLOSE_HOLIDAY",
                "NO_ELIGIBLE_SEGMENTS" => "SESSION_CLOSE_NO_ELIGIBLE_SEGMENTS",
                "ITERATION_ERROR" => "SESSION_CLOSE_ITERATION_ERROR",
                "NO_BARS" or "EMPTY_BARS" or "TRADING_HOURS_MISSING" or "TIMEZONE_ERROR" or "SESSION_ITERATOR_ERROR" or "SESSION_CALCULATION_ERROR" or "UNHANDLED_EXCEPTION" => "SESSION_CLOSE_RESOLUTION_FAILURE",
                _ => "SESSION_CLOSE_RESOLUTION_FAILURE"
            };
            var note = r.FailureReason switch
            {
                "HOLIDAY" => "Exchange holiday per TradingHours template",
                "NO_ELIGIBLE_SEGMENTS" => "Sessions exist but none overlap timetable window",
                "ITERATION_ERROR" => "Date resolution failed",
                "NO_BARS" => "Bars collection is null",
                "EMPTY_BARS" => "Bars collection is empty",
                "TRADING_HOURS_MISSING" => "Bars.TradingHours is null",
                "TIMEZONE_ERROR" => "Chicago timezone resolution failed",
                "SESSION_ITERATOR_ERROR" => "SessionIterator creation failed",
                "SESSION_CALCULATION_ERROR" => "Session iteration/calculation threw",
                "UNHANDLED_EXCEPTION" => "Unexpected exception in resolver",
                _ => "Resolution failed"
            };
            var failurePayload = new Dictionary<string, object?>
            {
                ["trading_day"] = tradingDay,
                ["session_class"] = sessionClass,
                ["failure_reason"] = r.FailureReason ?? "UNKNOWN",
                ["exception_type"] = r.ExceptionType ?? (object?)"",
                ["exception_message"] = r.ExceptionMessage ?? (object?)"",
                ["stack_trace_truncated"] = r.StackTraceTruncated ?? (object?)"",
                ["bars_count"] = r.BarsCount,
                ["bars_instrument"] = r.BarsInstrument ?? "N/A",
                ["trading_hours_name"] = r.TradingHoursName ?? "N/A",
                ["strategy_instance_id"] = r.StrategyInstanceId ?? "N/A",
                ["note"] = note
            };
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDay, eventType: eventType, state: "ENGINE", failurePayload));
            if (r.ExceptionType != null || !string.IsNullOrEmpty(r.ExceptionMessage) || !string.IsNullOrEmpty(r.StackTraceTruncated))
            {
                System.Diagnostics.Trace.TraceError($"[SessionClose] {r.FailureReason}: {r.ExceptionType ?? "N/A"} {r.ExceptionMessage ?? ""}\n{r.StackTraceTruncated ?? ""}");
            }
        }
    }

    private void RunSessionCloseGlobalExposureSweep(string tradingDateStr, string sessionClass, DateTimeOffset utcNow)
    {
        if (_executionAdapter == null || string.IsNullOrWhiteSpace(tradingDateStr) || string.IsNullOrWhiteSpace(sessionClass))
            return;

        if (IsPlaybackStallQuiescenceBlockingNtCalls())
        {
            TryLogSessionCloseGlobalSweepSkipped(tradingDateStr, sessionClass, _executionInstrument ?? "", "QUIESCENCE_ARMED", utcNow,
                new { note = "Playback quiescence is armed; global sweep will not start new NT-touching work." });
            return;
        }

        AccountSnapshot? snap;
        try
        {
            snap = _executionAdapter.GetAccountSnapshot(utcNow);
        }
        catch (Exception ex)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "SESSION_CLOSE_GLOBAL_SWEEP_ERROR", state: "ENGINE",
                new
                {
                    session_class = sessionClass,
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    phase = "account_snapshot"
                }));
            return;
        }

        if (snap == null)
            return;

        var candidateInstruments = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in snap.Positions ?? new List<PositionSnapshot>())
        {
            if (p == null || p.Quantity == 0 || string.IsNullOrWhiteSpace(p.Instrument))
                continue;
            candidateInstruments.Add(p.Instrument.Trim());
        }

        foreach (var w in snap.WorkingOrders ?? new List<WorkingOrderSnapshot>())
        {
            if (w == null || string.IsNullOrWhiteSpace(w.Instrument) || !IsRobotOwnedOrder(w))
                continue;
            candidateInstruments.Add(w.Instrument.Trim());
        }

        if (candidateInstruments.Count == 0)
            return;

        var openByInst = _executionJournal.GetOpenJournalEntriesByInstrument();
        foreach (var inst in candidateInstruments)
            TryRunSessionCloseGlobalExposureSweepForInstrument(openByInst, snap, tradingDateStr, sessionClass, inst, utcNow);
    }

    private void TryRunSessionCloseGlobalExposureSweepForInstrument(
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInst,
        AccountSnapshot snap,
        string tradingDateStr,
        string sessionClass,
        string instrument,
        DateTimeOffset utcNow)
    {
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst))
            return;

        var canonical = GetCanonicalInstrument(inst);
        var rows = CollectSessionCloseOpenJournalRows(openByInst, inst, canonical, tradingDateStr, sessionClass);
        var journalOpenQty = rows.Sum(r => r.Remaining);
        var brokerAbsQty = SumBrokerPositionQty(snap, inst);
        var brokerSignedQty = SumBrokerPositionSignedQty(snap, inst);
        var robotWorkingCount = CountRobotTaggedBrokerWorkingForInstrument(snap, inst);
        if (brokerAbsQty <= 0 && robotWorkingCount <= 0)
            return;
        if (!IsSessionCloseGlobalSweepInstrumentInEngineScope(inst, sessionClass, tradingDateStr))
        {
            TryLogSessionCloseGlobalSweepSkipped(tradingDateStr, sessionClass, inst, "OUTSIDE_ENGINE_SCOPE", utcNow,
                new
                {
                    instrument = inst,
                    canonical_instrument = canonical,
                    session_class = sessionClass,
                    broker_abs_qty = brokerAbsQty,
                    broker_signed_qty = brokerSignedQty,
                    robot_working_count = robotWorkingCount,
                    engine_execution_instrument = _executionInstrument ?? "",
                    engine_master_instrument = _masterInstrumentName ?? "",
                    note = "This strategy instance observed shared account exposure for another engine scope; global sweep is scoped to the owning engine."
                });
            return;
        }
        if (HasLocalActiveSessionCloseStreamForInstrument(inst, sessionClass))
            return;

        var intentIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.IntentId))
                intentIds.Add(row.IntentId.Trim());
        }
        foreach (var intentId in CollectRobotTaggedIntentIdsForInstrument(snap, inst))
            intentIds.Add(intentId);

        var hasActiveTrackedReentry = TryGetActiveTrackedReentrySweepBlocker(
            tradingDateStr,
            inst,
            intentIds,
            out var trackedStreams,
            out var trackedSessions,
            out var trackedReentryIntentIds);
        var hasRobotEvidence = journalOpenQty > 0 || intentIds.Count > 0;
        var activeReentryState = hasActiveTrackedReentry
            ? "TRACKED_ACTIVE_REENTRY"
            : "";
        var sweepAuthorityFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            FrameId = $"session_close_global_sweep:{tradingDateStr}:{sessionClass}:{inst}:{utcNow:O}",
            Source = "RobotEngine.SessionCloseGlobalSweep",
            Account = _accountName ?? "",
            Instrument = inst,
            CanonicalInstrument = canonical,
            ExecutionInstrumentKey = _executionInstrument ?? "",
            TradingDate = tradingDateStr,
            IntentId = intentIds.FirstOrDefault() ?? "",
            SubmitPath = "SESSION_CLOSE_GLOBAL_SWEEP",
            DecisionUtc = utcNow,
            FrameCreatedUtc = utcNow,
            BrokerSnapshotCapturedUtc = utcNow,
            BrokerPositionQty = brokerSignedQty,
            BrokerWorkingOrdersCount = robotWorkingCount,
            JournalOpenQty = journalOpenQty,
            ActiveIntentsCount = intentIds.Count,
            ActiveIntentIds = intentIds.ToArray(),
            ActiveReentryState = activeReentryState,
            SessionCloseState = sessionClass,
            AuthorityState = "SESSION_CLOSE_GLOBAL_SWEEP"
        });
        var sweepAuthority = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.SessionCloseGlobalSweep,
            Source = "RobotEngine.SessionCloseGlobalSweep",
            Instrument = inst,
            IntentId = sweepAuthorityFrame.IntentId,
            UtcNow = utcNow,
            HasActiveTrackedReentry = hasActiveTrackedReentry,
            HasRobotEvidence = hasRobotEvidence,
            BrokerAbsQty = brokerAbsQty,
            BrokerWorkingOrderCount = robotWorkingCount,
            JournalOpenQty = journalOpenQty,
            AuthorityFrame = sweepAuthorityFrame
        });
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "AUTHORITY_FRAME_SNAPSHOT", state: "ENGINE",
            ExecutionAuthorityFrameBuilder.ToLogPayload(
                sweepAuthorityFrame,
                action: "SESSION_CLOSE_GLOBAL_SWEEP",
                decision: sweepAuthority.Allowed ? "ALLOW" : "DENY",
                denyReason: sweepAuthority.DenyReason)));

        if (!sweepAuthority.Allowed &&
            string.Equals(sweepAuthority.DenyReason, "TRACKED_ACTIVE_REENTRY", StringComparison.OrdinalIgnoreCase))
        {
            var staleRequestKey = (tradingDateStr, inst);
            ClearSessionCloseGlobalSweepClaim(tradingDateStr, inst, staleRequestKey);
            TryLogSessionCloseGlobalSweepSkipped(tradingDateStr, sessionClass, inst, "TRACKED_ACTIVE_REENTRY", utcNow,
                new
                {
                    instrument = inst,
                    canonical_instrument = canonical,
                    session_class = sessionClass,
                    broker_abs_qty = brokerAbsQty,
                    broker_signed_qty = brokerSignedQty,
                    robot_working_count = robotWorkingCount,
                    journal_open_qty = journalOpenQty,
                    intent_ids = intentIds.ToArray(),
                    tracked_streams = trackedStreams,
                    tracked_sessions = trackedSessions,
                    tracked_reentry_intent_ids = trackedReentryIntentIds,
                    stale_global_sweep_claim_cleared = true,
                    authority_gate = sweepAuthority.GateName,
                    authority_deny_reason = sweepAuthority.DenyReason ?? "",
                    authority_detail = sweepAuthority.Detail ?? "",
                    note = "Broker exposure/working orders are explained by an active tracked reentry lifecycle; refusing session-close global sweep so a prior close event cannot flatten the resumed trade."
                });
            return;
        }

        if (!sweepAuthority.Allowed &&
            string.Equals(sweepAuthority.DenyReason, "NO_ROBOT_EVIDENCE", StringComparison.OrdinalIgnoreCase))
        {
            TryLogSessionCloseGlobalSweepSkipped(tradingDateStr, sessionClass, inst, "NO_ROBOT_EVIDENCE", utcNow,
                new
                {
                    instrument = inst,
                    canonical_instrument = canonical,
                    broker_abs_qty = brokerAbsQty,
                    broker_signed_qty = brokerSignedQty,
                    robot_working_count = robotWorkingCount,
                    authority_gate = sweepAuthority.GateName,
                    authority_deny_reason = sweepAuthority.DenyReason ?? "",
                    authority_detail = sweepAuthority.Detail ?? "",
                    note = "Broker exposure/working orders were visible, but no open journal row or QTSW2 intent tag tied them to this session."
                });
            return;
        }

        if (!sweepAuthority.Allowed)
        {
            TryLogSessionCloseGlobalSweepSkipped(tradingDateStr, sessionClass, inst, sweepAuthority.DenyReason ?? "AUTHORITY_DENIED", utcNow,
                new
                {
                    instrument = inst,
                    canonical_instrument = canonical,
                    broker_abs_qty = brokerAbsQty,
                    broker_signed_qty = brokerSignedQty,
                    journal_open_qty = journalOpenQty,
                    robot_working_count = robotWorkingCount,
                    authority_gate = sweepAuthority.GateName,
                    authority_deny_reason = sweepAuthority.DenyReason ?? "",
                    authority_detail = sweepAuthority.Detail ?? "",
                    note = "Central execution authority denied session-close global sweep."
                });
            return;
        }

        var primaryIntentId = rows
            .OrderByDescending(r => r.Remaining)
            .ThenBy(r => r.Stream, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.IntentId?.Trim() ?? "")
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (string.IsNullOrWhiteSpace(primaryIntentId))
            primaryIntentId = intentIds.FirstOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(primaryIntentId))
        {
            TryLogSessionCloseGlobalSweepSkipped(tradingDateStr, sessionClass, inst, "NO_INTENT_ID", utcNow,
                new
                {
                    instrument = inst,
                    broker_abs_qty = brokerAbsQty,
                    robot_working_count = robotWorkingCount,
                    journal_open_qty = journalOpenQty
                });
            return;
        }

        var requestKey = (tradingDateStr, inst);
        var retryingGlobalSweep = false;
        var claimSource = "new_claim";
        lock (_sessionCloseGlobalSweepLock)
        {
            if (!_sessionCloseGlobalSweepRequested.Add(requestKey))
            {
                if (!TryAllowSessionCloseGlobalSweepRetryLocked(requestKey, brokerAbsQty, utcNow))
                    return;
                retryingGlobalSweep = true;
                claimSource = "in_memory_marker_retry";
            }
            else
            {
                _sessionCloseGlobalSweepLastRequestUtc[requestKey] = utcNow;
            }
        }

        if (!retryingGlobalSweep && !_journals.TryMarkSessionCloseGlobalSweepRequested(tradingDateStr, inst))
        {
            lock (_sessionCloseGlobalSweepLock)
            {
                if (!TryAllowSessionCloseGlobalSweepRetryLocked(requestKey, brokerAbsQty, utcNow))
                    return;
                retryingGlobalSweep = true;
                claimSource = "durable_marker_retry";
            }
        }

        if (retryingGlobalSweep)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "SESSION_CLOSE_GLOBAL_SWEEP_RETRY", state: "ENGINE",
                new
                {
                    session_class = sessionClass,
                    instrument = inst,
                    canonical_instrument = canonical,
                    broker_abs_qty = brokerAbsQty,
                    broker_signed_qty = brokerSignedQty,
                    journal_open_qty = journalOpenQty,
                    robot_working_count = robotWorkingCount,
                    claim_source = claimSource,
                    retry_interval_seconds = SESSION_CLOSE_GLOBAL_SWEEP_RETRY_SECONDS,
                    note = "Broker exposure remained after a prior session-close global sweep claim; enqueueing a bounded retry."
                }));
        }

        var path = "";
        var extraCancelCount = 0;
        try
        {
            var extraIntentIds = intentIds
                .Where(id => !string.Equals(id, primaryIntentId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (extraIntentIds.Length > 0)
                extraCancelCount = _executionAdapter?.RequestSessionCloseCancelIntents(extraIntentIds, inst, utcNow) ?? 0;

            var sessionCloseRequest = _executionAdapter?.RequestSessionCloseFlattenImmediate(primaryIntentId, inst, utcNow);
            var accepted = sessionCloseRequest != null;
            path = accepted ? "session_close_global_enqueue" : "emergency_flatten_enqueue";
            if (!accepted)
                accepted = _executionAdapter?.TryEnqueueEmergencyFlattenProtective(inst, utcNow) == true;

            if (!accepted)
            {
                ClearSessionCloseGlobalSweepClaim(tradingDateStr, inst, requestKey);
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "SESSION_CLOSE_GLOBAL_SWEEP_ERROR", state: "ENGINE",
                    new
                    {
                        session_class = sessionClass,
                        instrument = inst,
                        primary_intent_id = primaryIntentId,
                        phase = "enqueue_flatten",
                        note = "No enqueue-safe session-close flatten path accepted the global sweep request."
                    }));
                return;
            }

            var marked = MarkSessionCloseInterruptedJournals(rows, primaryIntentId, utcNow, out var markedStreams);
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "SESSION_CLOSE_GLOBAL_SWEEP_REQUESTED", state: "ENGINE",
                new
                {
                    session_class = sessionClass,
                    instrument = inst,
                    canonical_instrument = canonical,
                    broker_abs_qty = brokerAbsQty,
                    broker_signed_qty = brokerSignedQty,
                    journal_open_qty = journalOpenQty,
                    robot_working_count = robotWorkingCount,
                    primary_intent_id = primaryIntentId,
                    intent_ids = intentIds.ToArray(),
                    extra_cancel_intents_requested = extraCancelCount,
                    stream_journals_marked = marked,
                    streams_marked = markedStreams,
                    authority_gate = sweepAuthority.GateName,
                    path,
                    note = "Broker-level session-close sweep requested because local stream flatten coverage was not sufficient."
                }));
        }
        catch (Exception ex)
        {
            ClearSessionCloseGlobalSweepClaim(tradingDateStr, inst, requestKey);
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "SESSION_CLOSE_GLOBAL_SWEEP_ERROR", state: "ENGINE",
                new
                {
                    session_class = sessionClass,
                    instrument = inst,
                    primary_intent_id = primaryIntentId,
                    path,
                    error = ex.Message,
                    exception_type = ex.GetType().Name
                }));
        }
    }

    private void ClearSessionCloseGlobalSweepClaim(string tradingDateStr, string inst, (string tradingDay, string instrument) requestKey)
    {
        lock (_sessionCloseGlobalSweepLock)
        {
            _sessionCloseGlobalSweepRequested.Remove(requestKey);
            _sessionCloseGlobalSweepLastRequestUtc.Remove(requestKey);
        }
        _journals.ClearSessionCloseGlobalSweepRequested(tradingDateStr, inst);
    }

    private bool TryAllowSessionCloseGlobalSweepRetryLocked(
        (string tradingDay, string instrument) requestKey,
        int brokerAbsQty,
        DateTimeOffset utcNow)
    {
        if (brokerAbsQty <= 0)
            return false;
        if (!_sessionCloseGlobalSweepLastRequestUtc.TryGetValue(requestKey, out var lastRequestUtc))
        {
            _sessionCloseGlobalSweepLastRequestUtc[requestKey] = utcNow;
            return false;
        }
        if ((utcNow - lastRequestUtc).TotalSeconds < SESSION_CLOSE_GLOBAL_SWEEP_RETRY_SECONDS)
            return false;

        _sessionCloseGlobalSweepLastRequestUtc[requestKey] = utcNow;
        return true;
    }

    private static List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry, int Remaining)>
        CollectSessionCloseOpenJournalRows(
            Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInst,
            string inst,
            string? canonical,
            string tradingDateStr,
            string sessionClass)
    {
        var rows = new List<(string, string, string, ExecutionJournalEntry, int)>();
        foreach (var kvp in openByInst)
        {
            if (!ExecutionJournal.OpenJournalMapBucketMatches(kvp.Key, inst, canonical))
                continue;
            foreach (var row in kvp.Value)
            {
                if (!string.Equals(row.TradingDate, tradingDateStr, StringComparison.Ordinal))
                    continue;
                if (!StreamMatchesSessionClass(row.Stream, sessionClass))
                    continue;
                var remaining = ExecutionJournal.GetEntryRemainingOpenQuantity(row.Entry);
                if (remaining <= 0)
                    continue;
                rows.Add((row.TradingDate, row.Stream, row.IntentId, row.Entry, remaining));
            }
        }

        return rows;
    }

    private static bool StreamMatchesSessionClass(string? stream, string sessionClass)
    {
        if (string.IsNullOrWhiteSpace(sessionClass))
            return true;
        var derived = TimetableStream.DeriveSessionFromStreamId(stream ?? "");
        return string.Equals(derived, sessionClass, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasLocalActiveSessionCloseStreamForInstrument(string instrument, string sessionClass)
    {
        lock (_engineLock)
        {
            return _streams.Values.Any(s =>
                !s.Committed &&
                string.Equals(s.Session, sessionClass, StringComparison.OrdinalIgnoreCase) &&
                ExecutionInstrumentResolver.IsSameInstrument(s.ExecutionInstrument, instrument));
        }
    }

    private bool TryGetActiveTrackedReentrySweepBlocker(
        string tradingDateStr,
        string instrument,
        IEnumerable<string> intentIds,
        out string[] trackedStreams,
        out string[] trackedSessions,
        out string[] trackedReentryIntentIds)
    {
        var inst = instrument?.Trim() ?? "";
        var intentSet = new HashSet<string>(
            intentIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()) ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var streams = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var reentryIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (_engineLock)
        {
            foreach (var stream in _streams.Values)
            {
                if (!stream.HasActiveReentryLifecycleEvidence)
                    continue;
                if (!ExecutionInstrumentResolver.IsSameInstrument(stream.ExecutionInstrument, inst))
                    continue;

                var reentryIntentId = stream.ReentryIntentId?.Trim() ?? "";
                var originalIntentId = stream.OriginalIntentId?.Trim() ?? "";
                var intentMatched = intentSet.Count == 0 ||
                                    (!string.IsNullOrWhiteSpace(reentryIntentId) && intentSet.Contains(reentryIntentId)) ||
                                    (!string.IsNullOrWhiteSpace(originalIntentId) && intentSet.Contains(originalIntentId));
                if (!intentMatched)
                    continue;

                streams.Add(stream.Stream);
                sessions.Add(stream.Session);
                if (!string.IsNullOrWhiteSpace(reentryIntentId))
                    reentryIds.Add(reentryIntentId);
            }
        }

        foreach (var journal in EnumerateStreamJournalsForTradingDate(tradingDateStr))
        {
            var reentryIntentId = journal.ReentryIntentId?.Trim() ?? "";
            var originalIntentId = journal.OriginalIntentId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(reentryIntentId))
                continue;
            if (!HasTrackedReentryJournalEvidence(journal))
                continue;

            var intentMatched = intentSet.Count == 0 ||
                                intentSet.Contains(reentryIntentId) ||
                                (!string.IsNullOrWhiteSpace(originalIntentId) && intentSet.Contains(originalIntentId));
            if (!intentMatched)
                continue;

            var entry = _executionJournal.GetEntry(reentryIntentId, journal.TradingDate, journal.Stream);
            if (entry == null || entry.TradeCompleted || ExecutionJournal.GetEntryRemainingOpenQuantity(entry) <= 0)
                continue;
            if (!ExecutionInstrumentResolver.IsSameInstrument(entry.Instrument, inst))
                continue;

            streams.Add(journal.Stream);
            sessions.Add(TimetableStream.DeriveSessionFromStreamId(journal.Stream));
            reentryIds.Add(reentryIntentId);
        }

        trackedStreams = streams.ToArray();
        trackedSessions = sessions.ToArray();
        trackedReentryIntentIds = reentryIds.ToArray();
        return trackedStreams.Length > 0;
    }

    private IEnumerable<StreamJournal> EnumerateStreamJournalsForTradingDate(string tradingDateStr)
    {
        if (string.IsNullOrWhiteSpace(tradingDateStr))
            yield break;

        string[] paths;
        try
        {
            var journalDir = RobotRunArtifactPaths.StateStreamJournals(_persistenceBase);
            if (!Directory.Exists(journalDir))
                yield break;
            paths = Directory.GetFiles(journalDir, $"{tradingDateStr}_*.json");
        }
        catch
        {
            yield break;
        }

        foreach (var path in paths)
        {
            StreamJournal? journal = null;
            try
            {
                journal = JsonUtil.Deserialize<StreamJournal>(File.ReadAllText(path));
            }
            catch
            {
                // Ignore unreadable stream journals here; reconciliation owns corruption escalation.
            }

            if (journal != null)
                yield return journal;
        }
    }

    private static bool HasTrackedReentryJournalEvidence(StreamJournal journal)
        => !string.IsNullOrWhiteSpace(journal.ReentryIntentId) &&
           (journal.ReentrySubmitPending ||
            journal.ReentrySubmitted ||
            journal.ReentryFilled ||
            journal.ProtectionSubmitted ||
            journal.ProtectionAccepted);

    private bool IsSessionCloseGlobalSweepInstrumentInEngineScope(string instrument, string sessionClass, string tradingDateStr)
    {
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(inst))
            return false;

        lock (_engineLock)
        {
            var hasExplicitScope =
                !string.IsNullOrWhiteSpace(_executionInstrument) ||
                !string.IsNullOrWhiteSpace(_masterInstrumentName) ||
                _streams.Count > 0;

            if (!hasExplicitScope)
                return true;

            if (!string.IsNullOrWhiteSpace(_executionInstrument) &&
                ExecutionInstrumentResolver.IsSameInstrument(_executionInstrument, inst))
                return true;

            if (!string.IsNullOrWhiteSpace(_masterInstrumentName) &&
                ExecutionInstrumentResolver.IsSameInstrument(_masterInstrumentName, inst))
                return true;

            return _streams.Values.Any(s =>
                string.Equals(s.Session, sessionClass, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.TradingDate, tradingDateStr, StringComparison.Ordinal) &&
                ExecutionInstrumentResolver.IsSameInstrument(s.ExecutionInstrument, inst));
        }
    }

    internal void RunSessionCloseGlobalExposureSweepForTest(IExecutionAdapter adapter, string tradingDateStr, string sessionClass, DateTimeOffset utcNow)
    {
        _executionAdapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        RunSessionCloseGlobalExposureSweep(tradingDateStr, sessionClass, utcNow);
    }

    private int MarkSessionCloseInterruptedJournals(
        List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry, int Remaining)> rows,
        string primaryIntentId,
        DateTimeOffset utcNow,
        out string[] markedStreams)
    {
        var marked = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.TradingDate) || string.IsNullOrWhiteSpace(row.Stream))
                continue;

            var rowOriginalIntentId = string.IsNullOrWhiteSpace(row.IntentId) ? primaryIntentId : row.IntentId;
            var activeStream = _streams.Values.FirstOrDefault(s =>
                !s.Committed &&
                string.Equals(s.TradingDate, row.TradingDate, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Stream, row.Stream, StringComparison.OrdinalIgnoreCase));
            if (activeStream != null &&
                activeStream.MarkSessionCloseInterruptedByGlobalSweep(utcNow, rowOriginalIntentId, "SESSION_CLOSE_GLOBAL_SWEEP"))
            {
                marked.Add(row.Stream);
                continue;
            }

            var journal = _journals.TryLoad(row.TradingDate, row.Stream);
            if (journal == null || journal.Committed)
                continue;

            journal.ExecutionInterruptedByClose = true;
            journal.ForcedFlattenTimestamp = utcNow;
            if (string.IsNullOrWhiteSpace(journal.OriginalIntentId))
                journal.OriginalIntentId = rowOriginalIntentId;
            journal.LastUpdateUtc = utcNow.ToString("o");
            if (_journals.Save(journal))
                marked.Add(row.Stream);
        }

        markedStreams = marked.ToArray();
        return marked.Count;
    }

    private void TryLogSessionCloseGlobalSweepSkipped(
        string tradingDateStr,
        string sessionClass,
        string instrument,
        string reason,
        DateTimeOffset utcNow,
        object payload)
    {
        var inst = instrument?.Trim() ?? "";
        lock (_sessionCloseGlobalSweepLock)
        {
            if (!_sessionCloseGlobalSweepSkipLogged.Add((tradingDateStr, sessionClass, inst, reason)))
                return;
        }

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "SESSION_CLOSE_GLOBAL_SWEEP_SKIPPED", state: "ENGINE",
            payload));
    }

    private (bool hasConflict, int sessionTotalStreams, int sessionEnabledStreams, int sessionCalendarBlockedStreams, string timetableTradingDay, string activeTradingDay)
        DetectNtHolidayConflict(string tradingDay, string sessionClass)
    {
        lock (_engineLock)
        {
            var activeTradingDay = _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
            if (_lastTimetable?.streams == null || _lastTimetable.streams.Count == 0)
            {
                var streamTotal = 0;
                var streamEnabled = 0;
                foreach (var stream in _streams.Values)
                {
                    if (!string.Equals(stream.Session, sessionClass, StringComparison.Ordinal))
                        continue;
                    if (!string.Equals(stream.TradingDate, tradingDay, StringComparison.Ordinal))
                        continue;
                    streamTotal++;
                    if (!stream.Committed)
                        streamEnabled++;
                }

                return (streamTotal > 0 && streamEnabled > 0, streamTotal, streamEnabled, 0, "", activeTradingDay);
            }

            var timetableTradingDay = _lastTimetable.GetSessionTradingDateForHashCompatibility();
            if (!string.Equals(activeTradingDay, tradingDay, StringComparison.Ordinal) &&
                !string.Equals(timetableTradingDay, tradingDay, StringComparison.Ordinal))
                return (false, 0, 0, 0, timetableTradingDay, activeTradingDay);

            var total = 0;
            var enabled = 0;
            var calendarBlocked = 0;
            foreach (var row in _lastTimetable.streams)
            {
                var rowSession = string.IsNullOrWhiteSpace(row.session)
                    ? TimetableStream.DeriveSessionFromStreamId(row.stream ?? "")
                    : row.session;
                if (!string.Equals(rowSession, sessionClass, StringComparison.Ordinal))
                    continue;
                total++;
                if (row.enabled)
                    enabled++;
                var reason = row.block_reason ?? "";
                if (!row.enabled && reason.StartsWith("calendar_filter_blocked", StringComparison.OrdinalIgnoreCase))
                    calendarBlocked++;
            }

            var hasConflict = total > 0 && enabled > 0;
            return (hasConflict, total, enabled, calendarBlocked, timetableTradingDay, activeTradingDay);
        }
    }

    private SessionCloseResult? BuildInternalSessionCloseOverride(string tradingDay)
    {
        if (!DateOnly.TryParse(tradingDay, out var td))
            return null;

        TimeService? timeService;
        string marketCloseTime;
        string marketReopenTime;
        string barsInstrument;
        lock (_engineLock)
        {
            timeService = _time;
            marketCloseTime = string.IsNullOrWhiteSpace(_spec?.entry_cutoff?.market_close_time) ? EMERGENCY_MARKET_CLOSE_DEFAULT : _spec.entry_cutoff.market_close_time;
            marketReopenTime = SessionTimingPolicy.ResolveMarketReopenTime(_spec);
            barsInstrument = _executionInstrument ?? "N/A";
        }
        if (timeService == null)
            return null;

        try
        {
            var closeChicago = timeService.ConstructChicagoTime(td, marketCloseTime);
            var closeUtc = timeService.ConvertChicagoToUtc(closeChicago);
            var flattenTriggerUtc = SessionTimingPolicy.ResolveForcedFlattenTriggerUtc(td, closeUtc, timeService, _spec, out var effectiveLeadSeconds);
            var reopenChicago = timeService.ConstructChicagoTime(td, marketReopenTime);
            var reopenUtc = timeService.ConvertChicagoToUtc(reopenChicago);
            return new SessionCloseResult
            {
                HasSession = true,
                FailureReason = null,
                BufferSeconds = effectiveLeadSeconds,
                ResolvedSessionCloseUtc = closeUtc,
                FlattenTriggerUtc = flattenTriggerUtc,
                NextSessionBeginUtc = reopenUtc,
                BarsInstrument = barsInstrument
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Set current session index for re-entry gate. Called by strategy each Realtime tick per sessionClass.
    /// </summary>
    public void SetCurrentSessionIndex(string tradingDay, string sessionClass, int index)
    {
        if (string.IsNullOrWhiteSpace(sessionClass) || sessionClass != "S1" && sessionClass != "S2") return;
        lock (_engineLock)
        {
            _currentSessionKeyByClass[sessionClass] = (tradingDay ?? "", index);
        }
    }

    /// <summary>
    /// Get reentry-allowed UTC time for (tradingDay, sessionClass).
    /// Returns (reentryUtc, hasSession). When hasSession=false, no reentry. When reentryUtc is null, caller should use spec fallback.
    /// </summary>
    public (DateTimeOffset? reentryUtc, bool hasSession) GetReentryAllowedUtc(string tradingDay, string sessionClass, DateTimeOffset utcNow)
    {
        var sessionTruth = ResolveSessionTruth(tradingDay, sessionClass, utcNow);
        if (!sessionTruth.IsResolved || sessionTruth.Result == null)
            return (null, false);
        if (!sessionTruth.HasSession)
            return (null, false);
        if (sessionTruth.NextSessionBeginUtc.HasValue)
            return (sessionTruth.NextSessionBeginUtc.Value, true);
        // Fallback: compute from spec market_reopen_time (same calendar day as market close)
        if (_spec != null && _time != null && DateOnly.TryParse(tradingDay, out var td))
        {
            var reopenStr = SessionTimingPolicy.ResolveMarketReopenTime(_spec);
            var reopenChicago = _time.ConstructChicagoTime(td, reopenStr);
            var reopenUtc = _time.ConvertChicagoToUtc(reopenChicago);
            return (reopenUtc, true);
        }
        return (null, true);
    }

    /// <summary>
    /// Try get cached session close result for (tradingDay, sessionClass).
    /// </summary>
    public bool TryGetSessionCloseResult(string tradingDay, string sessionClass, out SessionCloseResult? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(tradingDay) || string.IsNullOrWhiteSpace(sessionClass)) return false;
        lock (_engineLock)
        {
            if (_sessionCloseResults.TryGetValue((tradingDay, sessionClass), out var r))
            {
                result = r;
                return true;
            }
        }
        return false;
    }

    private static int SessionFlattenSourcePriority(string source) => source switch
    {
        "INTERNAL_CALENDAR" => 5,
        "INTERNAL_OVERRIDE" => 4,
        "OVERRIDE" => 4,
        "NT_CACHE" => 3,
        "SPEC" => 2,
        "DEFAULT_1600" => 1,
        _ => 0
    };

    /// <summary>
    /// Emit SESSION_CLOSE_SOURCE_SELECTED, SESSION_RESOLVED, and FLATTEN_TRIGGER_SET for Watchdog aggregation.
    /// Source priority prevents advisory NT cache from superseding higher internal authority.
    /// </summary>
    private void TryEmitSessionFlattenVisibility(
        string tradingDay,
        string sessionClass,
        string source,
        bool hasSession,
        DateTimeOffset? sessionCloseUtc,
        DateTimeOffset? flattenTriggerUtc,
        int bufferSeconds,
        DateTimeOffset utcNow,
        string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(tradingDay) || string.IsNullOrWhiteSpace(sessionClass) || _time == null)
            return;

        var key = (tradingDay, sessionClass);
        var pNew = SessionFlattenSourcePriority(source);
        lock (_engineLock)
        {
            if (_sessionFlattenVisibilitySource.TryGetValue(key, out var oldSrc))
            {
                var pOld = SessionFlattenSourcePriority(oldSrc);
                if (pNew < pOld)
                    return;
                if (pNew == pOld && string.Equals(oldSrc, source, StringComparison.Ordinal))
                    return;
            }
            _sessionFlattenVisibilitySource[key] = source;
        }

        var inst = _executionInstrument ?? "";
        var chicagoClose = "";
        var chicagoTrigger = "";
        if (sessionCloseUtc.HasValue)
            chicagoClose = _time.ConvertUtcToChicago(sessionCloseUtc.Value).ToString("o");
        if (flattenTriggerUtc.HasValue)
            chicagoTrigger = _time.ConvertUtcToChicago(flattenTriggerUtc.Value).ToString("o");

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDay, eventType: "SESSION_CLOSE_SOURCE_SELECTED", state: "ENGINE",
            new Dictionary<string, object?>
            {
                ["session_class"] = sessionClass,
                ["instrument"] = inst,
                ["source"] = source,
                ["has_session"] = hasSession
            }));

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDay, eventType: "SESSION_RESOLVED", state: "ENGINE",
            new Dictionary<string, object?>
            {
                ["session_class"] = sessionClass,
                ["instrument"] = inst,
                ["session_close_utc"] = sessionCloseUtc?.ToString("o") ?? "",
                ["session_close_chicago"] = chicagoClose,
                ["has_session"] = hasSession,
                ["reason"] = reason ?? ""
            }));

        if (hasSession && flattenTriggerUtc.HasValue)
        {
            var flattenPayloadCore = new Dictionary<string, object?>
            {
                ["session_class"] = sessionClass,
                ["flatten_trigger_utc"] = flattenTriggerUtc.Value.ToString("o"),
                ["flatten_trigger_chicago"] = chicagoTrigger,
                ["buffer_seconds"] = bufferSeconds
            };
            List<StreamStateMachine> flattenTargets;
            lock (_engineLock)
            {
                flattenTargets = _streams.Values
                    .Where(s => string.Equals(s.Session, sessionClass, StringComparison.Ordinal))
                    .ToList();
            }
            if (flattenTargets.Count > 0)
            {
                foreach (var s in flattenTargets)
                {
                    var payload = new Dictionary<string, object?>(flattenPayloadCore)
                    {
                        ["instrument"] = string.IsNullOrWhiteSpace(s.Instrument) ? inst : s.Instrument,
                        ["stream"] = s.Stream ?? ""
                    };
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDay, eventType: "FLATTEN_TRIGGER_SET", state: "ENGINE", payload));
                }
            }
            else
            {
                var payload = new Dictionary<string, object?>(flattenPayloadCore)
                {
                    ["instrument"] = inst,
                    ["stream"] = ""
                };
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDay, eventType: "FLATTEN_TRIGGER_SET", state: "ENGINE", payload));
            }
        }
    }

    private void TryEmitSessionCloseFallbackWarning(string tradingDay, string sessionClass, DateTimeOffset utcNow)
    {
        var key = (tradingDay, sessionClass);
        bool first;
        lock (_engineLock)
        {
            first = _sessionCloseFallbackWarningEmitted.Add(key);
        }
        if (!first) return;
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDay, eventType: "SESSION_CLOSE_FALLBACK_WARNING", state: "ENGINE",
            new Dictionary<string, object?>
            {
                ["session_class"] = sessionClass,
                ["instrument"] = _executionInstrument ?? "",
                ["source"] = "DEFAULT_1600",
                ["reason"] = "Potential holiday or misconfigured session — using hard 16:00 CT fallback",
                ["note"] = "Verify timetable, spec market_close_time, and NT TradingHours for this date"
            }));
    }

    /// <summary>
    /// CME equity index default close. Used when spec is null (emergency fail-closed).
    /// Spec market_close_time is America/Chicago local time (DST-aware via TimeService).
    /// </summary>
    private const string EMERGENCY_MARKET_CLOSE_DEFAULT = "16:00";

    private void EnsureInternalCalendarPolicyMaterializedForActiveDay(string tradingDay, DateTimeOffset utcNow)
    {
        if (_sessionPolicyService == null || _time == null || !_activeTradingDate.HasValue)
            return;
        if (!DateOnly.TryParse(tradingDay, out var td))
            return;
        if (td != _activeTradingDate.Value)
            return;
        TryMaterializeInternalCalendarPolicy(utcNow);
    }

    /// <summary>
    /// One row per (S1,S2) when <c>session_calendar.json</c> defines an override for the locked trading day and this engine's calendar group.
    /// Idempotent per <see cref="_internalCalendarPolicyMaterializedForDay"/>.
    /// </summary>
    private void TryMaterializeInternalCalendarPolicy(DateTimeOffset utcNow)
    {
        if (_sessionPolicyService == null || _time == null || !_activeTradingDate.HasValue)
            return;
        var day = _activeTradingDate.Value;
        if (_internalCalendarPolicyMaterializedForDay == day)
            return;
        _internalCalendarPolicyMaterializedForDay = day;
        lock (_engineLock)
        {
            _internalCalendarSessionClose.Clear();
        }

        var calendarGroup = CalendarGroupResolver.Resolve(_executionInstrument, _masterInstrumentName);
        var dayStr = day.ToString("yyyy-MM-dd");
        var barsInstrument = _executionInstrument ?? "N/A";
        var forcedFlattenBufferSeconds = SessionTimingPolicy.ResolveForcedFlattenBufferSeconds(_spec);

        foreach (var sessionClass in new[] { "S1", "S2" })
        {
            if (!_sessionPolicyService.TryBuildSessionClose(day, calendarGroup, _time, _spec, forcedFlattenBufferSeconds, barsInstrument, out var mat))
                continue;

            lock (_engineLock)
            {
                _internalCalendarSessionClose[(dayStr, sessionClass)] = mat;
            }

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: dayStr, eventType: "SESSION_POLICY_MATERIALIZED", state: "ENGINE",
                new Dictionary<string, object?>
                {
                    ["session_class"] = sessionClass,
                    ["calendar_group"] = calendarGroup,
                    ["session_calendar_path"] = _sessionCalendarPath,
                    ["has_session"] = mat.HasSession,
                    ["note"] = "session_calendar.json row applied; precedes NT session-close cache for this (trading_day, session_class)"
                }));

            if (mat.HasSession)
            {
                TryEmitSessionFlattenVisibility(dayStr, sessionClass, "INTERNAL_CALENDAR", true,
                    mat.ResolvedSessionCloseUtc, mat.FlattenTriggerUtc, mat.BufferSeconds, utcNow);
            }
            else
            {
                TryEmitSessionFlattenVisibility(dayStr, sessionClass, "INTERNAL_CALENDAR", false,
                    null, null, 0, utcNow, mat.FailureReason);
            }
        }
    }

    /// <summary>
    /// Resolve one authoritative session truth frame from internal calendar, NT evidence, and spec fallback.
    ///
    /// TIMEZONE CONTRACT (non-negotiable):
    /// - spec.entry_cutoff.market_close_time is the fallback session-close authority in America/Chicago local time.
    /// - spec.forced_flatten.buffer_seconds defines the lead time before session close.
    /// - TimeService.ConstructChicagoTime uses GetUtcOffset(localDateTime) for correct DST.
    /// - ConvertChicagoToUtc produces UTC for comparison with utcNow.
    /// - Never treat Chicago local as UTC.
    /// </summary>
    private static bool IsNtNoSessionFailure(string? reason)
    {
        var r = reason ?? "";
        return r == "HOLIDAY" ||
               r == "NO_ELIGIBLE_SEGMENTS" ||
               r == "ITERATION_ERROR" ||
               r == "NO_BARS" ||
               r == "EMPTY_BARS" ||
               r == "TRADING_HOURS_MISSING" ||
               r == "TIMEZONE_ERROR" ||
               r == "SESSION_ITERATOR_ERROR" ||
               r == "SESSION_CALCULATION_ERROR" ||
               r == "UNHANDLED_EXCEPTION";
    }

    private bool ShouldOverrideCachedNoSession(SessionCloseResult cached, bool activeTimetableConflict)
    {
        if (cached.HasSession || !activeTimetableConflict)
            return false;
        if (string.Equals(cached.FailureReason, "HOLIDAY", StringComparison.Ordinal))
            return _loggingConfig.prefer_internal_calendar_over_nt_holiday;
        return IsNtNoSessionFailure(cached.FailureReason);
    }

    private SessionTruthFrame BuildSessionTruthFrame(
        string tradingDay,
        string sessionClass,
        string source,
        SessionCloseResult result,
        bool usedFallback = false,
        bool conflictDetected = false,
        SessionCloseResult? ntCacheResult = null,
        SessionCloseResult? internalResult = null)
    {
        return new SessionTruthFrame
        {
            TradingDay = tradingDay,
            SessionClass = sessionClass,
            IsResolved = true,
            HasSession = result.HasSession,
            Source = source,
            AuthorityRank = SessionFlattenSourcePriority(source),
            UsedFallback = usedFallback,
            ConflictDetected = conflictDetected,
            FailureReason = result.HasSession ? null : result.FailureReason,
            Result = result,
            NtCacheResult = ntCacheResult,
            InternalResult = internalResult
        };
    }

    private void TryLogSessionTruthOverride(string tradingDay, string sessionClass, DateTimeOffset utcNow, SessionCloseResult cached, SessionCloseResult overridden)
    {
        var isHoliday = string.Equals(cached.FailureReason, "HOLIDAY", StringComparison.Ordinal);
        var eventType = isHoliday ? "SESSION_CLOSE_HOLIDAY_OVERRIDDEN" : "SESSION_CLOSE_NO_SESSION_OVERRIDDEN";
        bool shouldLog;
        lock (_engineLock)
        {
            shouldLog = _sessionTruthOverrideEmittedKeys.Add((tradingDay, sessionClass, eventType));
        }
        if (!shouldLog)
            return;

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDay, eventType: eventType, state: "ENGINE",
            new Dictionary<string, object?>
            {
                ["trading_day"] = tradingDay,
                ["session_class"] = sessionClass,
                ["cached_failure_reason"] = cached.FailureReason ?? "UNKNOWN",
                ["override_source"] = "INTERNAL_SPEC_RUNTIME",
                ["resolved_session_close_utc"] = overridden.ResolvedSessionCloseUtc?.ToString("o"),
                ["flatten_trigger_utc"] = overridden.FlattenTriggerUtc?.ToString("o"),
                ["next_session_begin_utc"] = overridden.NextSessionBeginUtc?.ToString("o"),
                ["buffer_seconds"] = overridden.BufferSeconds,
                ["note"] = isHoliday
                    ? "Runtime session truth overrode cached NinjaTrader HOLIDAY because timetable still has active streams"
                    : "Runtime session truth overrode cached NinjaTrader no-session result because timetable still has active streams"
            }));
    }

    public SessionTruthFrame ResolveSessionTruth(string tradingDay, string sessionClass, DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(tradingDay) || string.IsNullOrWhiteSpace(sessionClass))
            return SessionTruthFrame.Unresolved(tradingDay, sessionClass, "INVALID_REQUEST");

        var usedFallback = false;
        EnsureInternalCalendarPolicyMaterializedForActiveDay(tradingDay, utcNow);

        SessionCloseResult? internalCal;
        lock (_engineLock)
        {
            _internalCalendarSessionClose.TryGetValue((tradingDay, sessionClass), out internalCal);
        }
        if (internalCal != null)
        {
            TryEmitSessionFlattenVisibility(tradingDay, sessionClass, "INTERNAL_CALENDAR", internalCal.HasSession,
                internalCal.ResolvedSessionCloseUtc, internalCal.FlattenTriggerUtc, internalCal.BufferSeconds, utcNow,
                internalCal.HasSession ? null : internalCal.FailureReason);
            return BuildSessionTruthFrame(tradingDay, sessionClass, "INTERNAL_CALENDAR", internalCal, internalResult: internalCal);
        }

        if (TryGetSessionCloseResult(tradingDay, sessionClass, out var cached) && cached != null)
        {
            var conflict = false;
            if (!cached.HasSession)
            {
                var (hasConflict, _, _, _, _, _) = DetectNtHolidayConflict(tradingDay, sessionClass);
                conflict = hasConflict;
                if (ShouldOverrideCachedNoSession(cached, hasConflict))
                {
                    var overridden = BuildInternalSessionCloseOverride(tradingDay);
                    if (overridden != null)
                    {
                        usedFallback = true;
                        TryEmitSessionFlattenVisibility(tradingDay, sessionClass, "INTERNAL_OVERRIDE", true,
                            overridden.ResolvedSessionCloseUtc, overridden.FlattenTriggerUtc, overridden.BufferSeconds, utcNow);
                        TryLogSessionTruthOverride(tradingDay, sessionClass, utcNow, cached, overridden);
                        return BuildSessionTruthFrame(tradingDay, sessionClass, "INTERNAL_OVERRIDE", overridden,
                            usedFallback: true, conflictDetected: true, ntCacheResult: cached, internalResult: overridden);
                    }
                }
            }

            TryEmitSessionFlattenVisibility(tradingDay, sessionClass, "NT_CACHE", cached.HasSession,
                cached.ResolvedSessionCloseUtc, cached.FlattenTriggerUtc, cached.BufferSeconds, utcNow,
                cached.HasSession ? null : cached.FailureReason);
            return BuildSessionTruthFrame(tradingDay, sessionClass, "NT_CACHE", cached, conflictDetected: conflict, ntCacheResult: cached);
        }

        if (!DateOnly.TryParse(tradingDay, out var tradingDate))
            return SessionTruthFrame.Unresolved(tradingDay, sessionClass, "INVALID_TRADING_DAY");

        if (_spec != null && _time != null)
        {
            var marketCloseTime = _spec.entry_cutoff?.market_close_time;
            if (!string.IsNullOrWhiteSpace(marketCloseTime))
            {
                var result = ComputeFallbackFromTime(tradingDate, marketCloseTime, tradingDay, sessionClass, utcNow, "SPEC", ref usedFallback);
                if (result != null)
                {
                    TryEmitSessionFlattenVisibility(tradingDay, sessionClass, "SPEC", true,
                        result.ResolvedSessionCloseUtc, result.FlattenTriggerUtc, result.BufferSeconds, utcNow);
                    return BuildSessionTruthFrame(tradingDay, sessionClass, "SPEC", result, usedFallback: usedFallback, internalResult: result);
                }
            }
        }

        if (_time != null)
        {
            var failKey = (tradingDay, sessionClass);
            bool shouldLogFail;
            lock (_engineLock)
            {
                shouldLogFail = _sessionCloseFallbackFailedEmittedKeys.Add(failKey);
            }
            if (shouldLogFail)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDay, eventType: "SESSION_CLOSE_FALLBACK_FAILED", state: "ENGINE",
                    new
                    {
                        instrument = _executionInstrument ?? "N/A",
                        trading_date = tradingDay,
                        session_class = sessionClass,
                        reason = _spec == null ? "SPEC_NULL" : "MARKET_CLOSE_TIME_EMPTY",
                        emergency_close_used = EMERGENCY_MARKET_CLOSE_DEFAULT,
                        note = "Spec unavailable - using emergency 16:00 CT default (fail-closed)"
                    }));
            }
            var emergency = ComputeFallbackFromTime(tradingDate, EMERGENCY_MARKET_CLOSE_DEFAULT, tradingDay, sessionClass, utcNow, "EMERGENCY", ref usedFallback);
            if (emergency != null)
            {
                TryEmitSessionFlattenVisibility(tradingDay, sessionClass, "DEFAULT_1600", true,
                    emergency.ResolvedSessionCloseUtc, emergency.FlattenTriggerUtc, emergency.BufferSeconds, utcNow);
                TryEmitSessionCloseFallbackWarning(tradingDay, sessionClass, utcNow);
                return BuildSessionTruthFrame(tradingDay, sessionClass, "DEFAULT_1600", emergency, usedFallback: usedFallback, internalResult: emergency);
            }
        }

        return SessionTruthFrame.Unresolved(tradingDay, sessionClass, "NO_SESSION_TRUTH");
    }

    private SessionCloseResult? GetSessionCloseResultOrFallback(string tradingDay, string sessionClass, DateTimeOffset utcNow, out bool usedFallback)
    {
        var truth = ResolveSessionTruth(tradingDay, sessionClass, utcNow);
        usedFallback = truth.UsedFallback;
        return truth.IsResolved ? truth.Result : null;
    }

    /// <summary>
    /// Compute session close from (tradingDate, hhmm) in America/Chicago local time.
    /// hhmm is interpreted as spec timezone (America/Chicago). DST-aware.
    /// </summary>
    private SessionCloseResult? ComputeFallbackFromTime(DateOnly tradingDate, string hhmm, string tradingDay, string sessionClass, DateTimeOffset utcNow, string source, ref bool usedFallback)
    {
        try
        {
            // America/Chicago local → UTC (deterministic, DST-aware)
            var sessionEndChicago = _time!.ConstructChicagoTime(tradingDate, hhmm);
            var sessionEndUtc = _time.ConvertChicagoToUtc(sessionEndChicago);
            var flattenTriggerUtc = SessionTimingPolicy.ResolveForcedFlattenTriggerUtc(tradingDate, sessionEndUtc, _time, _spec, out var effectiveLeadSeconds);
            var reopenStr = SessionTimingPolicy.ResolveMarketReopenTime(_spec);
            var reopenChicago = _time.ConstructChicagoTime(tradingDate, reopenStr);
            var nextSessionBeginUtc = _time.ConvertChicagoToUtc(reopenChicago);
            var fallback = new SessionCloseResult
            {
                HasSession = true,
                FlattenTriggerUtc = flattenTriggerUtc,
                ResolvedSessionCloseUtc = sessionEndUtc,
                NextSessionBeginUtc = nextSessionBeginUtc,
                BufferSeconds = effectiveLeadSeconds,
                BarsInstrument = _executionInstrument ?? "N/A"
            };
            var key = (tradingDay, sessionClass);
            bool shouldLog;
            lock (_engineLock)
            {
                shouldLog = _sessionCloseFallbackEmittedKeys.Add(key);
            }
            if (shouldLog)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDay, eventType: "SESSION_CLOSE_FALLBACK_USED", state: "ENGINE",
                    new
                    {
                        instrument = _executionInstrument ?? "N/A",
                        trading_date = tradingDay,
                        session_class = sessionClass,
                        computed_session_close_utc = sessionEndUtc.ToString("o"),
                        flatten_trigger_utc = flattenTriggerUtc.ToString("o"),
                        resolution_source = source,
                        timezone = "America/Chicago",
                        note = "Session close from fallback — ResolveAndSetSessionCloseIfNeeded did not populate cache"
                    }));
            }
            usedFallback = true;
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Try get current session index for sessionClass. Used by stream re-entry gate.
    /// </summary>
    public bool TryGetCurrentSessionIndex(string sessionClass, out string tradingDay, out int index)
    {
        tradingDay = "";
        index = 0;
        if (string.IsNullOrWhiteSpace(sessionClass)) return false;
        lock (_engineLock)
        {
            if (_currentSessionKeyByClass.TryGetValue(sessionClass, out var tuple))
            {
                tradingDay = tuple.tradingDay;
                index = tuple.index;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Get session start time for an instrument, with fallback to default.
    /// </summary>
    /// <param name="instrument">Instrument name</param>
    /// <returns>Session start time in HH:MM format</returns>
    private string GetSessionStartTime(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument))
            return "17:00"; // Default fallback

        var instrumentUpper = instrument.ToUpperInvariant();
        if (_sessionStartTimes.TryGetValue(instrumentUpper, out var startTime))
            return startTime;

        // Fallback to default (standard CME futures session start)
        return "17:00";
    }

    /// <summary>
    /// Compute the trading session window for a given trading date.
    /// Session starts the evening before (from TradingHours or default 17:00 CST) and ends at market close (16:00 CST).
    /// </summary>
    /// <param name="tradingDate">Trading date</param>
    /// <param name="instrument">Instrument name (for instrument-specific session start time)</param>
    /// <returns>Tuple of (sessionStartChicago, sessionEndChicago)</returns>
    private (DateTimeOffset sessionStartChicago, DateTimeOffset sessionEndChicago) GetSessionWindow(DateOnly tradingDate, string instrument = "")
    {
        if (_spec is null || _time is null)
            throw new InvalidOperationException("Spec and TimeService must be initialized");

        // Session starts previous calendar day at time from TradingHours (or default 17:00 CST)
        var sessionStartTime = GetSessionStartTime(instrument);
        var previousDay = tradingDate.AddDays(-1);
        var sessionStartChicago = _time.ConstructChicagoTime(previousDay, sessionStartTime);

        // Session ends at market close on trading date (from spec)
        var marketCloseTime = _spec.entry_cutoff.market_close_time; // "16:00"
        var sessionEndChicago = _time.ConstructChicagoTime(tradingDate, marketCloseTime);

        return (sessionStartChicago, sessionEndChicago);
    }

    /// <summary>
    /// Get session information from spec for a given instrument and session.
    /// Used by NinjaTrader strategy to get range_start_time and slot_end_times for BarsRequest.
    /// </summary>
    /// <param name="instrument">Instrument code (e.g., "ES")</param>
    /// <param name="session">Session name (e.g., "S1")</param>
    /// <returns>Tuple of (rangeStartTime, slotEndTimes) or null if not found</returns>
    public (string rangeStartTime, List<string> slotEndTimes)? GetSessionInfo(string instrument, string session)
    {
        lock (_engineLock)
        {
            if (_spec is null) return null;

            // Check if instrument exists in spec
            if (!_spec.TryGetInstrument(instrument, out _))
                return null;

            // Check if session exists in spec
            if (!_spec.sessions.ContainsKey(session))
                return null;

            var sessionInfo = _spec.sessions[session];
            return (sessionInfo.range_start_time, sessionInfo.slot_end_times);
        }
    }
}
