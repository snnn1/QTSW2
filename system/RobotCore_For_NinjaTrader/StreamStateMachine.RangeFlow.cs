using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Threading;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Execution;

public sealed partial class StreamStateMachine
{
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

                        if (!Commit(utcNow, "NO_TRADE_LATE_START_MISSED_BREAKOUT", "NO_TRADE_LATE_START_MISSED_BREAKOUT")) return;
                        return; // Do not transition to ARMED
                    }

                    // P2.10: bar-based missed-breakout passed — align with unified tick rule vs breakout stops (last bar close if no top-of-book).
                    if (isLateStart && reconstructedRangeHigh.HasValue && reconstructedRangeLow.HasValue)
                    {
                        var brkL = UtilityRoundToTick.RoundToTick(reconstructedRangeHigh.Value + _tickSize, _tickSize);
                        var brkS = UtilityRoundToTick.RoundToTick(reconstructedRangeLow.Value - _tickSize, _tickSize);
                        decimal? hbBid = null, hbAsk = null;
                        if (_executionAdapter != null)
                        {
                            var t = _executionAdapter.GetCurrentMarketPrice(ExecutionInstrument, utcNow);
                            hbBid = t.Bid;
                            hbAsk = t.Ask;
                        }
                        if (!hbBid.HasValue && !hbAsk.HasValue)
                        {
                            var snap = GetBarBufferSnapshot();
                            if (snap.Count > 0)
                            {
                                snap.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
                                var c = snap[snap.Count - 1].Close;
                                hbBid = c;
                                hbAsk = c;
                            }
                        }
                        if (!LogAndEvaluateUnifiedBreakoutEntryValidity(utcNow, "LATE_HYDRATION", failClosedOnMissingQuotes: false,
                                brkL, brkS, hbBid, hbAsk))
                        {
                            if (!Commit(utcNow, "NO_TRADE_LATE_START_UNIFIED_BREAKOUT_INVALID", "NO_TRADE_LATE_START_UNIFIED_BREAKOUT_INVALID")) return;
                            return;
                        }
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

                // P2.10: after bar-based missed-breakout scan, unify with same tick rule (DRYRUN path; SIM path above).
                if (!missedBreakout && isLateStart && reconstructedRangeHigh.HasValue && reconstructedRangeLow.HasValue)
                {
                    var brkL = UtilityRoundToTick.RoundToTick(reconstructedRangeHigh.Value + _tickSize, _tickSize);
                    var brkS = UtilityRoundToTick.RoundToTick(reconstructedRangeLow.Value - _tickSize, _tickSize);
                    decimal? hbBid = null, hbAsk = null;
                    if (_executionAdapter != null)
                    {
                        var t = _executionAdapter.GetCurrentMarketPrice(ExecutionInstrument, utcNow);
                        hbBid = t.Bid;
                        hbAsk = t.Ask;
                    }
                    if (!hbBid.HasValue && !hbAsk.HasValue)
                    {
                        var snap = GetBarBufferSnapshot();
                        if (snap.Count > 0)
                        {
                            snap.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
                            var c = snap[snap.Count - 1].Close;
                            hbBid = c;
                            hbAsk = c;
                        }
                    }
                    if (!LogAndEvaluateUnifiedBreakoutEntryValidity(utcNow, "LATE_HYDRATION", failClosedOnMissingQuotes: false,
                            brkL, brkS, hbBid, hbAsk))
                    {
                        if (!Commit(utcNow, "NO_TRADE_LATE_START_UNIFIED_BREAKOUT_INVALID", "NO_TRADE_LATE_START_UNIFIED_BREAKOUT_INVALID")) return;
                        return;
                    }
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
                        if (TryCommitCompletedTradeAtMarketClose(utcNow)) return;
                        if (HasEntryFillForCurrentStream()) return;
                        LogNoTradeMarketClose(utcNow);
                        if (!Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE")) return;
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
                    _lastRangeComputeFailedLogUtc = null;

                    // Persist RANGE_BUILDING snapshot for restart recovery
                    var barsAtStart = GetBarBufferSnapshot();
                    if (barsAtStart.Count > 0)
                    {
                        var lastBarAtStart = barsAtStart[barsAtStart.Count - 1];
                        PersistRangeBuildingSnapshot(lastBarAtStart.TimestampUtc);
                    }

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
        if (utcNow >= MarketCloseUtc)
        {
            if (TryCommitCompletedTradeAtMarketClose(utcNow)) return;
            if (HasEntryFillForCurrentStream()) return;
            LogNoTradeMarketClose(utcNow);
            if (!Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE")) return;
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

            // Legacy check: If range is locked but state is not RANGE_LOCKED, log critical error.
            // A durable terminal commit intentionally moves RANGE_LOCKED -> DONE and is not a partial lock failure.
            if (_rangeLocked && State != StreamState.RANGE_LOCKED && !(State == StreamState.DONE && _journal.Committed))
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
        EnsureCommittedForPostLockExcursion(utcNow);

        if (utcNow < SlotTimeUtc)
        {
            var shouldLog = !_lastRangeLockedPreSlotWaitLogUtc.HasValue ||
                            (utcNow - _lastRangeLockedPreSlotWaitLogUtc.Value).TotalMinutes >= 30.0;
            if (shouldLog)
            {
                _lastRangeLockedPreSlotWaitLogUtc = utcNow;
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "RANGE_LOCKED_PRE_SLOT_WAIT", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        trading_date = TradingDate,
                        now_utc = utcNow.ToString("o"),
                        slot_time_utc = SlotTimeUtc.ToString("o"),
                        note = "RANGE_LOCKED state exists before slot time, usually from playback rewind/restart restoration. Side effects are deferred until slot time."
                    }));
            }
            return;
        }

        // Phase B: Execute pending recovery action (invariant-based model)
        if (_entryOrderRecoveryState.IsPending && _executionAdapter != null)
        {
            try
            {
                var snap = _executionAdapter.GetAccountSnapshot(utcNow);
                if (ExecutePendingRecoveryAction(snap, utcNow))
                    return; // Action handled (resubmit or cancel sent)
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDERS_RESUBMIT_POSITION_CHECK_ERROR", State.ToString(),
                    new { error = ex.Message, note = "Phase B execution failed - will retry next tick" }));
            }
        }

        // RESTART RECOVERY: Retry stop bracket placement if it failed previously
        // This handles the case where stop orders failed to place (e.g., missing policy expectations)
        // and the strategy was restarted. On restart, we retry placement if:
        // - Stop brackets weren't submitted yet (_stopBracketsSubmittedAtLock = false)
        // - Entry not detected
        // - Before market close
        // - Range and breakout levels are available
        if (!_stopBracketsSubmittedAtLock && !_entryDetected && utcNow >= SlotTimeUtc && utcNow < MarketCloseUtc &&
            RangeHigh.HasValue && RangeLow.HasValue &&
            _brkLongRounded.HasValue && _brkShortRounded.HasValue)
        {
            // Check if intents were already submitted (idempotency check)
            var longIntentId = ComputeIntentId("Long", _brkLongRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_LONG");
            var shortIntentId = ComputeIntentId("Short", _brkShortRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_SHORT");

            bool alreadySubmitted = false;
            if (_executionJournal != null)
            {
                alreadySubmitted = _executionJournal.IsIntentSubmitted(longIntentId, TradingDate, Stream) ||
                                  _executionJournal.IsIntentSubmitted(shortIntentId, TradingDate, Stream);
            }

            if (!alreadySubmitted)
            {
                if (IsPostLockBreakoutSetupExpired())
                {
                    _ = Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED");
                }
                else
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
        }

        // Check for market close cutoff (all execution modes)
        if (utcNow >= MarketCloseUtc)
        {
            if (TryCommitCompletedTradeAtMarketClose(utcNow)) return;
            if (HasEntryFillForCurrentStream()) return;
            LogNoTradeMarketClose(utcNow);
            if (!Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE")) return;
        }
    }
}
