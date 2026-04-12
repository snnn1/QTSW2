#if NINJATRADER

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NinjaTrader.Data;

namespace QTSW2.Robot.Core;

/// <summary>
/// Resolves session close for forced flatten using NinjaTrader SessionIterator.
/// Flatten: structural close from largest session gap (no timetable overlap, no time-of-day heuristic).
/// Slot classification: IsTimetableEligibleSegment and GetCurrentSessionIndex still use timetable.
/// Defer to Realtime only (avoid DataLoaded/Historical lag).
/// </summary>
public static class SessionCloseResolver
{
    private const int DefaultBufferSeconds = 300;
    private const int MaxStackTraceLength = 1500;

    /// <summary>
    /// Check if a TradingHours segment overlaps the timetable-eligible window for sessionClass.
    /// S1: [02:00, slot_end]; S2: [08:00, slot_end].
    /// Overlap logic only (any overlap); no "nearest" logic.
    /// </summary>
    public static bool IsTimetableEligibleSegment(
        DateTime segmentStartChicago,
        DateTime segmentEndChicago,
        string sessionClass,
        ParitySpec paritySpec)
    {
        if (paritySpec?.sessions == null) return false;
        if (sessionClass != "S1" && sessionClass != "S2") return false;
        if (!paritySpec.sessions.TryGetValue(sessionClass, out var sessionInfo))
            return false;

        var rangeStartStr = sessionInfo.range_start_time ?? "";
        var slotEndTimes = sessionInfo.slot_end_times ?? new List<string>();
        var latestSlotEnd = slotEndTimes.Count > 0
            ? slotEndTimes.OrderByDescending(s => s, StringComparer.Ordinal).First()
            : "16:00";

        if (string.IsNullOrWhiteSpace(rangeStartStr)) return false;

        // Parse range [rangeStart, latestSlotEnd] on target trading day
        // Use a reference date for parsing HH:mm
        var refDate = segmentStartChicago.Date;
        if (!TimeSpan.TryParse(rangeStartStr.Trim(), out var rangeStartTs))
            return false;
        if (!TimeSpan.TryParse(latestSlotEnd.Trim(), out var rangeEndTs))
            return false;

        var windowStart = refDate.Add(rangeStartTs);
        var windowEnd = refDate.Add(rangeEndTs);
        // Handle overnight: e.g. 02:00-08:00 next day
        if (rangeEndTs < rangeStartTs)
            windowEnd = windowEnd.AddDays(1);

        // Overlap: segment overlaps [windowStart, windowEnd)
        var segStart = segmentStartChicago;
        var segEnd = segmentEndChicago;
        return segStart < windowEnd && segEnd > windowStart;
    }

    /// <summary>
    /// Resolve session close for flatten using SessionIterator.
    /// Structural close: largest gap between segments defines the close (segment end before that gap).
    /// No timetable overlap, no time-of-day heuristic. If no segments = full holiday.
    /// Returns SessionCloseResult; HasSession=false with structured FailureReason taxonomy.
    /// </summary>
    public static SessionCloseResult Resolve(
        Bars bars,
        ParitySpec paritySpec,
        string sessionClass,
        string targetTradingDay,
        int bufferSeconds = DefaultBufferSeconds,
        string? strategyInstanceId = null)
    {
        var result = new SessionCloseResult { BufferSeconds = bufferSeconds, StrategyInstanceId = strategyInstanceId };

        try
        {
            return ResolveCore(bars, paritySpec, sessionClass, targetTradingDay, bufferSeconds, result);
        }
        catch (Exception ex)
        {
            result.HasSession = false;
            result.FailureReason = "UNHANDLED_EXCEPTION";
            PopulateExceptionMetadata(result, ex, bars);
            TraceSessionCloseResolutionFailure(result);
            Trace.TraceError($"[SessionCloseResolver] UNHANDLED_EXCEPTION: {ex}\n{ex.StackTrace}");
            return result;
        }
    }

    private static SessionCloseResult ResolveCore(
        Bars? bars,
        ParitySpec paritySpec,
        string sessionClass,
        string targetTradingDay,
        int bufferSeconds,
        SessionCloseResult result)
    {
        // Input validation (no try)
        if (paritySpec == null || (sessionClass != "S1" && sessionClass != "S2"))
        {
            result.HasSession = false;
            result.FailureReason = "ITERATION_ERROR";
            return result;
        }

        if (!DateTime.TryParse(targetTradingDay, out var targetDate))
        {
            result.HasSession = false;
            result.FailureReason = "ITERATION_ERROR";
            return result;
        }

        // Defensive guards before any SessionIterator usage (no try)
        if (bars == null)
        {
            result.HasSession = false;
            result.FailureReason = "NO_BARS";
            result.BarsCount = 0;
            result.BarsInstrument = "N/A";
            result.TradingHoursName = null;
            result.ExceptionMessage = null;
            result.ExceptionType = null;
            result.StackTraceTruncated = null;
            TraceSessionCloseResolutionFailure(result);
            return result;
        }

        result.BarsCount = bars.Count;
        result.BarsInstrument = GetInstrumentNameFromBars(bars);
        result.TradingHoursName = GetTradingHoursNameFromBars(bars);

        if (bars.Count == 0)
        {
            result.HasSession = false;
            result.FailureReason = "EMPTY_BARS";
            result.ExceptionMessage = null;
            result.ExceptionType = null;
            result.StackTraceTruncated = null;
            TraceSessionCloseResolutionFailure(result);
            return result;
        }

        if (GetTradingHoursFromBars(bars) == null)
        {
            result.HasSession = false;
            result.FailureReason = "TRADING_HOURS_MISSING";
            result.ExceptionMessage = null;
            result.ExceptionType = null;
            result.StackTraceTruncated = null;
            TraceSessionCloseResolutionFailure(result);
            return result;
        }

        // Timezone resolution (narrow try/catch)
        TimeZoneInfo? tz;
        try
        {
            var (resolvedTz, tzEx) = ResolveChicagoTimeZone();
            if (resolvedTz == null)
            {
                result.HasSession = false;
                result.FailureReason = "TIMEZONE_ERROR";
                PopulateExceptionMetadata(result, tzEx, bars);
                TraceSessionCloseResolutionFailure(result);
                Trace.TraceError($"[SessionCloseResolver] TIMEZONE_ERROR: {tzEx?.Message ?? "Unknown"}\n{tzEx?.StackTrace ?? ""}");
                return result;
            }
            tz = resolvedTz;
        }
        catch (Exception ex)
        {
            result.HasSession = false;
            result.FailureReason = "TIMEZONE_ERROR";
            PopulateExceptionMetadata(result, ex, bars);
            TraceSessionCloseResolutionFailure(result);
            Trace.TraceError($"[SessionCloseResolver] TIMEZONE_ERROR: {ex}\n{ex.StackTrace}");
            return result;
        }

        // SessionIterator creation (narrow try/catch)
        SessionIterator si;
        try
        {
            si = new SessionIterator(bars);
        }
        catch (Exception ex)
        {
            result.HasSession = false;
            result.FailureReason = "SESSION_ITERATOR_ERROR";
            PopulateExceptionMetadata(result, ex, bars);
            TraceSessionCloseResolutionFailure(result);
            Trace.TraceError($"[SessionCloseResolver] SESSION_ITERATOR_ERROR: {ex}\n{ex.StackTrace}");
            return result;
        }

        // Session iteration logic (narrow try/catch)
        try
        {
            var segments = new List<(DateTimeOffset BeginUtc, DateTimeOffset EndUtc)>();
            var sawTargetDay = false;
            DateTimeOffset? nextSessionBeginUtc = null;

            var cursor = targetDate.Date.AddDays(-1).AddHours(18);
            var maxIterations = 200;
            var iter = 0;

            while (iter++ < maxIterations)
            {
                si.CalculateTradingDay(cursor, true);
                var tradingDayExchange = si.ActualTradingDayExchange;
                var tradingDayStr = tradingDayExchange.ToString("yyyy-MM-dd");

                var begin = si.ActualSessionBegin;
                var end = si.ActualSessionEnd;

                if (string.CompareOrdinal(tradingDayStr, targetTradingDay) > 0)
                {
                    // First segment of next day = next session begin (market reopen for reentry)
                    var beginOffset = new DateTimeOffset(begin, tz.GetUtcOffset(begin));
                    nextSessionBeginUtc = beginOffset.ToUniversalTime();
                    break;
                }

                if (tradingDayStr == targetTradingDay)
                {
                    sawTargetDay = true;
                    var beginOffset = new DateTimeOffset(begin, tz.GetUtcOffset(begin));
                    var endOffset = new DateTimeOffset(end, tz.GetUtcOffset(end));
                    segments.Add((beginOffset.ToUniversalTime(), endOffset.ToUniversalTime()));
                }

                si.GetNextSession(si.ActualSessionEnd, true);
                cursor = si.ActualSessionBegin;
            }

            if (segments.Count == 0)
            {
                result.HasSession = false;
                result.FlattenTriggerUtc = null;
                result.FailureReason = sawTargetDay ? "NO_ELIGIBLE_SEGMENTS" : "HOLIDAY";
                return result;
            }

            DateTimeOffset closeEndUtc;
            double largestGapMinutes = 0;

            if (segments.Count == 1)
            {
                closeEndUtc = segments[0].EndUtc;
            }
            else
            {
                var sorted = segments.OrderBy(s => s.BeginUtc).ToList();
                var indexOfLargestGap = -1;
                var maxGap = TimeSpan.Zero;

                for (var i = 0; i < sorted.Count - 1; i++)
                {
                    var gap = sorted[i + 1].BeginUtc - sorted[i].EndUtc;
                    if (gap > maxGap)
                    {
                        maxGap = gap;
                        indexOfLargestGap = i;
                    }
                }

                if (indexOfLargestGap >= 0 && maxGap > TimeSpan.Zero)
                {
                    closeEndUtc = sorted[indexOfLargestGap].EndUtc;
                    largestGapMinutes = maxGap.TotalMinutes;
                    // Next session begin = segment after the gap (for reentry time gate)
                    if (!nextSessionBeginUtc.HasValue)
                        nextSessionBeginUtc = sorted[indexOfLargestGap + 1].BeginUtc;
                }
                else
                {
                    closeEndUtc = sorted[sorted.Count - 1].EndUtc;
                    Trace.TraceWarning($"[SessionCloseResolver] Resolve fallback: segments.Count={segments.Count} but no positive gap found. Using last segment EndUtc.");
                }
            }

            var flattenTriggerUtc = closeEndUtc.AddSeconds(-bufferSeconds);
            result.ResolvedSessionCloseUtc = closeEndUtc;
            result.FlattenTriggerUtc = flattenTriggerUtc;
            result.NextSessionBeginUtc = nextSessionBeginUtc;
            result.HasSession = true;

            var closeEndChicago = TimeZoneInfo.ConvertTimeFromUtc(closeEndUtc.UtcDateTime, tz);
            var flattenTriggerChicago = TimeZoneInfo.ConvertTimeFromUtc(flattenTriggerUtc.UtcDateTime, tz);
            Trace.TraceInformation($"[SessionCloseResolver] Resolve: instrument={result.BarsInstrument} tradingDay={targetTradingDay} segmentCount={segments.Count} chosenCloseEndChicago={closeEndChicago:yyyy-MM-dd HH:mm:ss} largestGapMinutes={largestGapMinutes:F1} flattenTriggerChicago={flattenTriggerChicago:yyyy-MM-dd HH:mm:ss}");
            TraceSessionCloseResolutionSuccess(result, targetTradingDay, sessionClass);
        }
        catch (Exception ex)
        {
            result.HasSession = false;
            result.FailureReason = "SESSION_CALCULATION_ERROR";
            PopulateExceptionMetadata(result, ex, bars);
            TraceSessionCloseResolutionFailure(result);
            Trace.TraceError($"[SessionCloseResolver] SESSION_CALCULATION_ERROR: {ex}\n{ex.StackTrace}");
        }

        return result;
    }

    /// <summary>
    /// Try "Central Standard Time" first, fallback to "America/Chicago".
    /// Returns (tz, null) on success; (null, exception) when both fail.
    /// </summary>
    private static (TimeZoneInfo? tz, Exception? ex) ResolveChicagoTimeZone()
    {
        Exception? lastEx = null;
        foreach (var id in new[] { "Central Standard Time", "America/Chicago" })
        {
            try
            {
                return (TimeZoneInfo.FindSystemTimeZoneById(id), null);
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }
        return (null, lastEx ?? new TimeZoneNotFoundException("Could not resolve Chicago timezone."));
    }

    private static void PopulateExceptionMetadata(SessionCloseResult result, Exception? ex, Bars? bars)
    {
        result.ExceptionType = ex?.GetType()?.FullName ?? null;
        result.ExceptionMessage = ex?.Message ?? null;
        var stack = ex?.StackTrace;
        result.StackTraceTruncated = string.IsNullOrEmpty(stack)
            ? null
            : stack.Length <= MaxStackTraceLength ? stack : stack.Substring(0, MaxStackTraceLength);
        if (bars != null)
        {
            result.BarsCount = bars.Count;
            result.BarsInstrument = GetInstrumentNameFromBars(bars);
            result.TradingHoursName = GetTradingHoursNameFromBars(bars);
        }
    }

    private static void TraceSessionCloseResolutionSuccess(SessionCloseResult result, string tradingDay, string sessionClass)
    {
        Trace.TraceInformation($"[SessionCloseResolver] SESSION_CLOSE_RESOLUTION_SUCCESS trading_day={tradingDay} session_class={sessionClass} instrument={result.BarsInstrument} bars_count={result.BarsCount}");
    }

    private static void TraceSessionCloseResolutionFailure(SessionCloseResult result)
    {
        Trace.TraceWarning($"[SessionCloseResolver] SESSION_CLOSE_RESOLUTION_FAILURE failure_reason={result.FailureReason} exception_type={result.ExceptionType ?? "N/A"} exception_message={result.ExceptionMessage ?? "N/A"} bars_count={result.BarsCount} bars_instrument={result.BarsInstrument ?? "N/A"} trading_hours_name={result.TradingHoursName ?? "N/A"} strategy_instance_id={result.StrategyInstanceId ?? "N/A"}");
    }

    private static string GetInstrumentNameFromBars(Bars bars)
    {
        try
        {
            if (bars == null) return "N/A";
            var inst = (bars as dynamic)?.Instrument;
            if (inst == null) return "N/A";
            return (string)(inst.FullName ?? inst.MasterInstrument?.Name ?? "N/A");
        }
        catch
        {
            return "N/A";
        }
    }

    private static object? GetTradingHoursFromBars(Bars bars)
    {
        try
        {
            if (bars == null) return null;
            return (bars as dynamic)?.TradingHours;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetTradingHoursNameFromBars(Bars bars)
    {
        try
        {
            var th = GetTradingHoursFromBars(bars);
            if (th == null) return null;
            return (th as dynamic)?.Name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get current session index for sessionClass at the given local time.
    /// SessionIndexWithinDay = count of timetable-eligible segments for that day up to and including current segment.
    /// Returns null if not in an eligible segment.
    /// </summary>
    public static (string tradingDay, int index)? GetCurrentSessionIndex(
        Bars bars,
        ParitySpec paritySpec,
        string sessionClass,
        DateTime timeLocalChicago)
    {
        if (bars == null || paritySpec == null || (sessionClass != "S1" && sessionClass != "S2"))
            return null;

        try
        {
            var si = new SessionIterator(bars);
            var cursor = timeLocalChicago.Date.AddHours(6);
            var maxIterations = 200;
            var iter = 0;
            var currentTradingDay = "";
            var indexInDay = 0;

            while (iter++ < maxIterations)
            {
                si.CalculateTradingDay(cursor, true);
                var tradingDayStr = si.ActualTradingDayExchange.ToString("yyyy-MM-dd");
                var begin = si.ActualSessionBegin;
                var end = si.ActualSessionEnd;

                if (IsTimetableEligibleSegment(begin, end, sessionClass, paritySpec))
                {
                    if (tradingDayStr != currentTradingDay)
                    {
                        currentTradingDay = tradingDayStr;
                        indexInDay = 0;
                    }
                    indexInDay++;

                    if (timeLocalChicago >= begin && timeLocalChicago < end)
                        return (tradingDayStr, indexInDay);
                }

                si.GetNextSession(si.ActualSessionEnd, true);
                cursor = si.ActualSessionBegin;
                if (cursor > timeLocalChicago.AddDays(1))
                    break;
            }
        }
        catch { }

        return null;
    }
}

#endif
