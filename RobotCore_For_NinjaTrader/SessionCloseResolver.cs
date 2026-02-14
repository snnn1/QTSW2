#if NINJATRADER

using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Data;

namespace QTSW2.Robot.Core;

/// <summary>
/// Resolves session close for forced flatten using NinjaTrader SessionIterator.
/// Per sessionClass (S1, S2); uses timetable-eligible segment overlap only.
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
    /// Resolve session close for (targetTradingDay, sessionClass) using SessionIterator.
    /// Returns SessionCloseResult; HasSession=false for holiday.
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
            return result;
        }

        if (!DateTime.TryParse(targetTradingDay, out var targetDate))
        {
            result.HasSession = false;
            return result;
        }

        try
        {
            var si = new SessionIterator(bars);
            var segments = new List<(DateTime begin, DateTime end, string tradingDay)>();

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
                    if (IsTimetableEligibleSegment(begin, end, sessionClass, paritySpec))
                        segments.Add((begin, end, tradingDayStr));
                }

                // Advance to next session
                si.GetNextSession(si.ActualSessionEnd, true);
                cursor = si.ActualSessionBegin;
            }

            if (segments.Count == 0)
            {
                result.HasSession = false;
                return result;
            }

            var lastSegment = segments.OrderBy(s => s.end).Last();
            // ActualSessionEnd is in user/local timezone; assume Chicago for CME
            var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
            var closeOffset = new DateTimeOffset(lastSegment.end, tz.GetUtcOffset(lastSegment.end));
            result.ResolvedSessionCloseUtc = closeOffset.ToUniversalTime();
            result.FlattenTriggerUtc = result.ResolvedSessionCloseUtc.Value.AddSeconds(-bufferSeconds);
            result.HasSession = true;
        }
        catch
        {
            result.HasSession = false;
        }

        return result;
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
