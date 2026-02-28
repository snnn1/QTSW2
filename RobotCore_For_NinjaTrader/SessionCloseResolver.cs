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
    /// Returns SessionCloseResult; HasSession=false with FailureReason: HOLIDAY, NO_ELIGIBLE_SEGMENTS, ITERATION_ERROR, or EXCEPTION.
    /// </summary>
    public static SessionCloseResult Resolve(
        Bars bars,
        ParitySpec paritySpec,
        string sessionClass,
        string targetTradingDay,
        int bufferSeconds = DefaultBufferSeconds)
    {
        var result = new SessionCloseResult { BufferSeconds = bufferSeconds };

        if (bars == null || paritySpec == null || (sessionClass != "S1" && sessionClass != "S2"))
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

        try
        {
            // Windows uses "Central Standard Time"; Linux/macOS use "America/Chicago"
            var tz = ResolveChicagoTimeZone();
            var si = new SessionIterator(bars);
            var segments = new List<(DateTimeOffset BeginUtc, DateTimeOffset EndUtc)>();
            var sawTargetDay = false;

            // Start from day before target to ensure we capture first segment of target day
            var cursor = targetDate.Date.AddDays(-1).AddHours(18); // Evening before
            var maxIterations = 200; // Safety limit
            var iter = 0;

            while (iter++ < maxIterations)
            {
                si.CalculateTradingDay(cursor, true);
                var tradingDayExchange = si.ActualTradingDayExchange;
                var tradingDayStr = tradingDayExchange.ToString("yyyy-MM-dd");

                if (string.CompareOrdinal(tradingDayStr, targetTradingDay) > 0)
                    break; // Advanced beyond target

                var begin = si.ActualSessionBegin;
                var end = si.ActualSessionEnd;
                if (tradingDayStr == targetTradingDay)
                {
                    sawTargetDay = true;
                    // Flatten: collect ALL segments (no timetable filter, no time-of-day filter)
                    var beginOffset = new DateTimeOffset(begin, tz.GetUtcOffset(begin));
                    var endOffset = new DateTimeOffset(end, tz.GetUtcOffset(end));
                    segments.Add((beginOffset.ToUniversalTime(), endOffset.ToUniversalTime()));
                }

                // Advance to next session
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
                // Sort by BeginUtc ascending
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
                }
                else
                {
                    // Fallback: no positive gap found (should not happen)
                    closeEndUtc = sorted[sorted.Count - 1].EndUtc;
                    Trace.TraceWarning($"[SessionCloseResolver] Resolve fallback: segments.Count={segments.Count} but no positive gap found. Using last segment EndUtc.");
                }
            }

            var flattenTriggerUtc = closeEndUtc.AddSeconds(-bufferSeconds);
            result.ResolvedSessionCloseUtc = closeEndUtc;
            result.FlattenTriggerUtc = flattenTriggerUtc;
            result.HasSession = true;

            // Debug log at resolution time
            var closeEndChicago = TimeZoneInfo.ConvertTimeFromUtc(closeEndUtc.UtcDateTime, tz);
            var flattenTriggerChicago = TimeZoneInfo.ConvertTimeFromUtc(flattenTriggerUtc.UtcDateTime, tz);
            var instrument = GetInstrumentNameFromBars(bars);
            Trace.TraceInformation($"[SessionCloseResolver] Resolve: instrument={instrument} tradingDay={targetTradingDay} segmentCount={segments.Count} chosenCloseEndChicago={closeEndChicago:yyyy-MM-dd HH:mm:ss} largestGapMinutes={largestGapMinutes:F1} flattenTriggerChicago={flattenTriggerChicago:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception ex)
        {
            result.HasSession = false;
            result.FailureReason = "EXCEPTION";
            result.ExceptionMessage = ex.Message;
        }

        return result;
    }

    private static TimeZoneInfo ResolveChicagoTimeZone()
    {
        foreach (var id in new[] { "Central Standard Time", "America/Chicago" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* try next */ }
        }
        throw new TimeZoneNotFoundException("Could not resolve Chicago timezone.");
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
