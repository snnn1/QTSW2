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

    private void PerformPreHydration(DateTimeOffset utcNow)
    {
        
        try
        {
            var nowChicago = _time.ConvertUtcToChicago(utcNow);
            
            // Compute hydration window: [range_start, min(now, range_end))
            var hydrationStart = RangeStartChicagoTime;
            var hydrationEnd = nowChicago < SlotTimeChicagoTime ? nowChicago : SlotTimeChicagoTime;
            
            // Resolve project root using deterministic method (not CWD)
            string projectRoot;
            try
            {
                projectRoot = ProjectRootResolver.ResolveProjectRoot();
            }
            catch (Exception ex)
            {
                LogHealth("ERROR", "PRE_HYDRATION_FAILED", $"Failed to resolve project root: {ex.Message}",
                    new
                    {
                        instrument = Instrument,
                        slot = Stream,
                        trading_date = TradingDate
                    });
                _preHydrationComplete = true; // Mark complete to allow progression (will fail later if needed)
                return;
            }
            
            // Construct file path: data/raw/{instrument}/1m/{yyyy}/{MM}/{yyyy-MM-dd}.csv
            var tradingDateParts = TradingDate.Split('-');
            if (tradingDateParts.Length != 3)
            {
                LogHealth("ERROR", "PRE_HYDRATION_FAILED", "Invalid trading date format",
                    new { instrument = Instrument, slot = Stream, trading_date = TradingDate });
                _preHydrationComplete = true;
                return;
            }
            
            if (!int.TryParse(tradingDateParts[0], out var year) ||
                !int.TryParse(tradingDateParts[1], out var month) ||
                !int.TryParse(tradingDateParts[2], out var day))
            {
                LogHealth("ERROR", "PRE_HYDRATION_FAILED", "Failed to parse trading date",
                    new { instrument = Instrument, slot = Stream, trading_date = TradingDate });
                _preHydrationComplete = true;
                return;
            }
            
            var fileDir = Path.Combine(projectRoot, "data", "raw", Instrument.ToLowerInvariant(), "1m", year.ToString("0000"), month.ToString("00"));
            // File naming pattern: {INSTRUMENT}_1m_{yyyy-MM-dd}.csv (e.g., ES_1m_2026-01-13.csv)
            var fileName = $"{Instrument.ToUpperInvariant()}_1m_{year:0000}-{month:00}-{day:00}.csv";
            var filePath = Path.Combine(fileDir, fileName);
            
            // Log fully resolved absolute path before reading
            var absolutePath = Path.GetFullPath(filePath);
            LogHealth("INFO", "PRE_HYDRATION_START", "Starting pre-hydration",
                new
                {
                    instrument = Instrument,
                    slot = Stream,
                    trading_date = TradingDate,
                    resolved_file_path = absolutePath,
                    hydration_start_chicago = hydrationStart.ToString("o"),
                    hydration_end_chicago = hydrationEnd.ToString("o")
                });
            
            if (!File.Exists(filePath))
            {
                var logLevel = nowChicago >= RangeStartChicagoTime ? "ERROR" : "WARN";
                LogHealth(logLevel, "PRE_HYDRATION_ZERO_BARS", "Pre-hydration file not found - zero bars loaded",
                    new
                    {
                        instrument = Instrument,
                        slot = Stream,
                        resolved_file_path = absolutePath,
                        hydration_start_chicago = hydrationStart.ToString("o"),
                        hydration_end_chicago = hydrationEnd.ToString("o"),
                        now_chicago = nowChicago.ToString("o"),
                        range_start_chicago = RangeStartChicagoTime.ToString("o")
                    });
                _hadZeroBarHydration = true; // Mark zero-bar hydration
                _preHydrationComplete = true;
                return;
            }
            
            // Read and parse CSV line-by-line
            var hydratedBars = new List<Bar>();
            var chicagoTz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
            
            using (var reader = new StreamReader(filePath))
            {
                // Skip header line
                var header = reader.ReadLine();
                if (header == null)
                {
                    var logLevel = nowChicago >= RangeStartChicagoTime ? "ERROR" : "WARN";
                    LogHealth(logLevel, "PRE_HYDRATION_ZERO_BARS", "Pre-hydration file empty (no header) - zero bars loaded",
                        new
                        {
                            instrument = Instrument,
                            slot = Stream,
                            resolved_file_path = absolutePath,
                            hydration_start_chicago = hydrationStart.ToString("o"),
                            hydration_end_chicago = hydrationEnd.ToString("o")
                        });
                    _hadZeroBarHydration = true; // Mark zero-bar hydration
                    _preHydrationComplete = true;
                    return;
                }
                
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    
                    var parts = line.Split(',');
                    if (parts.Length < 5) continue;
                    
                    // Parse timestamp_utc (bar open time in UTC)
                    if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestampUtc))
                        continue;
                    
                    // Convert to Chicago time (bar OPEN time)
                    var barOpenChicago = TimeZoneInfo.ConvertTimeFromUtc(timestampUtc.DateTime, chicagoTz);
                    var barOpenChicagoOffset = new DateTimeOffset(barOpenChicago, chicagoTz.GetUtcOffset(barOpenChicago));
                    
                    // Filter to hydration window [hydration_start, hydration_end)
                    if (barOpenChicagoOffset < hydrationStart || barOpenChicagoOffset >= hydrationEnd)
                        continue;
                    
                    // Parse OHLCV
                    if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open) ||
                        !decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var high) ||
                        !decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var low) ||
                        !decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close))
                        continue;
                    
                    decimal? volume = null;
                    if (parts.Length > 5 && decimal.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var vol))
                        volume = vol;
                    
                    var bar = new Bar(timestampUtc, open, high, low, close, volume);
                    hydratedBars.Add(bar);
                }
            }
            
            // Sort by UTC timestamp
            hydratedBars.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
            
            // Insert hydrated bars into the same buffer used by OnBarUpdate (no source tagging)
            // Insert hydrated bars into the same buffer used by OnBarUpdate
            // Track as CSV bars (from file-based pre-hydration)
            // CRITICAL: All deduplication logic is centralized in AddBarToBuffer()
            // This ensures consistent precedence enforcement (LIVE > BARSREQUEST > CSV)
            foreach (var bar in hydratedBars)
            {
                AddBarToBuffer(bar, BarSource.CSV);
            }
            
            // Sort buffer to maintain chronological order (after all additions)
            lock (_barBufferLock)
            {
                _barBuffer.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
            }
            
            // Reset gap counters after pre-hydration completes
            _largestSingleGapMinutes = 0.0;
            _totalGapMinutes = 0.0;
            _lastBarOpenChicago = null;
            
            // Log PRE_HYDRATION summary
            var barSpanStart = hydratedBars.Count > 0 ? _time.ConvertUtcToChicago(hydratedBars[0].TimestampUtc) : (DateTimeOffset?)null;
            var barSpanEnd = hydratedBars.Count > 0 ? _time.ConvertUtcToChicago(hydratedBars[hydratedBars.Count - 1].TimestampUtc) : (DateTimeOffset?)null;
            
            if (hydratedBars.Count == 0)
            {
                var logLevel = nowChicago >= RangeStartChicagoTime ? "ERROR" : "WARN";
                LogHealth(logLevel, "PRE_HYDRATION_ZERO_BARS", "Pre-hydration loaded zero bars",
                    new
                    {
                        instrument = Instrument,
                        slot = Stream,
                        resolved_file_path = absolutePath,
                        hydration_start_chicago = hydrationStart.ToString("o"),
                        hydration_end_chicago = hydrationEnd.ToString("o"),
                        now_chicago = nowChicago.ToString("o"),
                        range_start_chicago = RangeStartChicagoTime.ToString("o")
                    });
                _hadZeroBarHydration = true; // Mark zero-bar hydration
            }
            else
            {
                LogHealth("INFO", "PRE_HYDRATION_COMPLETE", $"Pre-hydration complete - {hydratedBars.Count} bars loaded",
                    new
                    {
                        instrument = Instrument,
                        slot = Stream,
                        trading_date = TradingDate,
                        resolved_file_path = absolutePath,
                        bars_loaded = hydratedBars.Count,
                        bar_span_start_chicago = barSpanStart?.ToString("o"),
                        bar_span_end_chicago = barSpanEnd?.ToString("o"),
                        hydration_start_chicago = hydrationStart.ToString("o"),
                        hydration_end_chicago = hydrationEnd.ToString("o"),
                        timestamp_chicago = nowChicago.ToString("o")
                    });
            }
            
            _preHydrationComplete = true;
        }
        catch (Exception ex)
        {
            LogHealth("ERROR", "PRE_HYDRATION_ERROR", $"Pre-hydration failed with exception: {ex.Message}",
                new
                {
                    instrument = Instrument,
                    slot = Stream,
                    trading_date = TradingDate,
                    error = ex.ToString()
                });
            _preHydrationComplete = true; // Mark complete to allow progression
        }
    }
    
    /// <summary>
    /// Check if stream has sufficient bars for range calculation.
    /// </summary>
    /// <param name="actualCount">Actual bar count in buffer</param>
    /// <param name="expected">Output: Expected bar count based on time window</param>
    /// <param name="required">Output: Minimum required bar count (based on threshold)</param>
    /// <param name="thresholdPercent">Threshold percentage (default 0.85 = 85%)</param>
    /// <returns>True if actualCount >= required, false otherwise</returns>
    private bool HasSufficientRangeBars(int actualCount, out int expected, out int required, double thresholdPercent = 0.85)
    {
        var rangeDurationMinutes = (SlotTimeChicagoTime - RangeStartChicagoTime).TotalMinutes;
        expected = Math.Max(0, (int)Math.Floor(rangeDurationMinutes));
        required = expected > 0 ? (int)Math.Ceiling(expected * thresholdPercent) : 0;
        return actualCount >= required;
    }
    
    /// <summary>
    /// Check if bars have gaps greater than the specified maximum gap.
    /// </summary>
    private bool HasGaps(List<(Bar Bar, DateTimeOffset OpenChicago)> bars, TimeSpan maxGap)
    {
        if (bars.Count < 2) return false;
        
        for (int i = 0; i < bars.Count - 1; i++)
        {
            var gap = bars[i + 1].OpenChicago - bars[i].OpenChicago;
            if (gap > maxGap) return true;
        }
        return false;
    }
    
    /// <summary>
    /// REFACTORED: Compute range retrospectively from completed session data.
    /// Queries all bars in [RangeStartChicago, endTimeChicago) and computes range in one pass.
    /// CRITICAL: Bar filtering uses Chicago time, not UTC, to match trading session semantics.
    /// </summary>
    private (bool Success, decimal? RangeHigh, decimal? RangeLow, decimal? FreezeClose, string FreezeCloseSource, int BarCount, string? Reason, DateTimeOffset? FirstBarUtc, DateTimeOffset? LastBarUtc, DateTimeOffset? FirstBarChicago, DateTimeOffset? LastBarChicago) ComputeRangeRetrospectively(DateTimeOffset utcNow, DateTimeOffset? endTimeUtc = null)
    {
        var bars = new List<Bar>();
        
        // Determine end time for range computation (default to slot_time, but can be current time for hybrid init)
        var endTimeUtcActual = endTimeUtc ?? SlotTimeUtc;
        var endTimeChicagoActual = _time.ConvertUtcToChicago(endTimeUtcActual);
        
        // Use bar buffer for range computation (bars from pre-hydration + OnBar())
        bars.AddRange(GetBarBufferSnapshot());

        // CRITICAL: Filter bars using Chicago time, not UTC
        // Convert each bar timestamp to Chicago time explicitly
        // OPTIMIZATION: Store Chicago time with bar to avoid redundant conversion in freeze close loop
        var filteredBars = new List<Bar>();
        var barChicagoTimes = new Dictionary<Bar, DateTimeOffset>(); // Cache Chicago time per bar
        DateTimeOffset? firstBarRawUtc = null;
        DateTimeOffset? lastBarRawUtc = null;
        DateTimeOffset? firstBarChicago = null;
        DateTimeOffset? lastBarChicago = null;
        string? firstBarRawUtcKind = null;
        string? lastBarRawUtcKind = null;
        
        // Get expected trading date from journal (should match timetable)
        var expectedTradingDate = TradingDate; // Format: "YYYY-MM-DD"
        
        // Track filtering statistics for diagnostics
        int barsFilteredByDate = 0;
        int barsFilteredByTimeWindow = 0;
        int barsAccepted = 0;
        DateTimeOffset? firstFilteredBarUtc = null;
        DateTimeOffset? lastFilteredBarUtc = null;
        string? firstFilteredBarReason = null;
        
        foreach (var bar in bars)
        {
            // Capture raw timestamp as received (assumed UTC)
            var barRawUtc = bar.TimestampUtc;
            var barRawUtcKind = barRawUtc.DateTime.Kind.ToString();
            
            // Convert to Chicago time for filtering
            var barChicagoTime = _time.ConvertUtcToChicago(barRawUtc);
            
            // CRITICAL: Filter by trading date first - only process bars from the correct trading date
            var barTradingDate = _time.GetChicagoDateToday(barRawUtc).ToString("yyyy-MM-dd");
            if (barTradingDate != expectedTradingDate)
            {
                // Bar is from wrong trading date - skip it
                // This ensures we only compute ranges from bars matching the timetable trading date
                barsFilteredByDate++;
                if (firstFilteredBarUtc == null)
                {
                    firstFilteredBarUtc = barRawUtc;
                    firstFilteredBarReason = $"Date mismatch: bar date {barTradingDate} != expected {expectedTradingDate}";
                }
                lastFilteredBarUtc = barRawUtc;
                continue;
            }
            
            // Range window is defined in Chicago time: [RangeStartChicagoTime, endTimeChicagoActual)
            // For hybrid initialization, endTime can be current time (not just slot_time)
            
            // DIAGNOSTIC: Proof log for 1-minute boundary investigation (retrospective computation)
            var comparisonResultRetro = barChicagoTime >= RangeStartChicagoTime && barChicagoTime < endTimeChicagoActual;
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "BAR_ADMISSION_PROOF_RETROSPECTIVE", State.ToString(),
                new
                {
                    bar_time_raw_utc = barRawUtc.ToString("o"),
                    bar_time_raw_kind = barRawUtcKind,
                    bar_time_chicago = barChicagoTime.ToString("o"),
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    range_end_chicago = endTimeChicagoActual.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                    comparison_result = comparisonResultRetro,
                    comparison_detail = comparisonResultRetro
                        ? $"bar_chicago ({barChicagoTime:HH:mm:ss}) >= range_start ({RangeStartChicagoTime:HH:mm:ss}) AND bar_chicago < end_time ({endTimeChicagoActual:HH:mm:ss})"
                        : $"bar_chicago ({barChicagoTime:HH:mm:ss}) NOT in [range_start ({RangeStartChicagoTime:HH:mm:ss}), end_time ({endTimeChicagoActual:HH:mm:ss}))",
                    bar_source = "CSV",
                    note = "Diagnostic proof log - bar timestamps represent OPEN time (CSV bars from translator already use open time)"
                }));
            
            if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < endTimeChicagoActual)
            {
                filteredBars.Add(bar);
                barChicagoTimes[bar] = barChicagoTime; // Cache Chicago time for reuse
                barsAccepted++;
                
                // Capture diagnostic info for first and last filtered bars
                if (firstBarRawUtc == null)
                {
                    firstBarRawUtc = barRawUtc;
                    firstBarRawUtcKind = barRawUtcKind;
                    firstBarChicago = barChicagoTime;
                }
                lastBarRawUtc = barRawUtc;
                lastBarRawUtcKind = barRawUtcKind;
                lastBarChicago = barChicagoTime;
            }
            else
            {
                // Bar is from correct date but outside time window
                barsFilteredByTimeWindow++;
                if (firstFilteredBarUtc == null && barsFilteredByDate == 0)
                {
                    firstFilteredBarUtc = barRawUtc;
                    var barTimeStr = barChicagoTime.ToString("HH:mm:ss");
                    var rangeStartStr = RangeStartChicagoTime.ToString("HH:mm:ss");
                    var rangeEndStr = endTimeChicagoActual.ToString("HH:mm:ss");
                    firstFilteredBarReason = $"Time window: bar time {barTimeStr} not in [{rangeStartStr}, {rangeEndStr})";
                }
                lastFilteredBarUtc = barRawUtc;
            }
        }
        
        // Log bar filtering details if bars were filtered out (rate-limited, diagnostic only)
        if (_enableDiagnosticLogs && (barsFilteredByDate > 0 || barsFilteredByTimeWindow > 0))
        {
            var shouldLogFiltering = !_lastBarFilteringLogUtc.HasValue || 
                                    (utcNow - _lastBarFilteringLogUtc.Value).TotalMinutes >= 1.0;
            if (shouldLogFiltering)
            {
                _lastBarFilteringLogUtc = utcNow;
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "RANGE_COMPUTE_BAR_FILTERING", State.ToString(),
                    new
                    {
                        bars_in_buffer = bars.Count,
                        bars_accepted = barsAccepted,
                        bars_filtered_by_date = barsFilteredByDate,
                        bars_filtered_by_time_window = barsFilteredByTimeWindow,
                        expected_trading_date = expectedTradingDate,
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        range_end_chicago = endTimeChicagoActual.ToString("o"),
                        first_filtered_bar_utc = firstFilteredBarUtc?.ToString("o"),
                        last_filtered_bar_utc = lastFilteredBarUtc?.ToString("o"),
                        first_filtered_bar_reason = firstFilteredBarReason,
                        note = $"Bar filtering details: {barsAccepted} accepted, {barsFilteredByDate} filtered by date, {barsFilteredByTimeWindow} filtered by time window"
                    }));
            }
        }
        
        bars = filteredBars;

        if (bars.Count == 0)
        {
            // Diagnostic: Log detailed information when no bars found in range window
            // This helps diagnose date mismatch or timing issues
            var barBufferCount = 0;
            DateTimeOffset? firstBarInBufferUtc = null;
            DateTimeOffset? lastBarInBufferUtc = null;
            DateTimeOffset? firstBarInBufferChicago = null;
            DateTimeOffset? lastBarInBufferChicago = null;
            string? barBufferDateRange = null;
            
            var bufferSnapshot = GetBarBufferSnapshot();
            barBufferCount = bufferSnapshot.Count;
            if (bufferSnapshot.Count > 0)
            {
                firstBarInBufferUtc = bufferSnapshot[0].TimestampUtc;
                lastBarInBufferUtc = bufferSnapshot[bufferSnapshot.Count - 1].TimestampUtc;
                firstBarInBufferChicago = _time.ConvertUtcToChicago(firstBarInBufferUtc.Value);
                lastBarInBufferChicago = _time.ConvertUtcToChicago(lastBarInBufferUtc.Value);
                
                var firstDate = _time.GetChicagoDateToday(firstBarInBufferUtc.Value);
                var lastDate = _time.GetChicagoDateToday(lastBarInBufferUtc.Value);
                if (firstDate == lastDate)
                {
                    barBufferDateRange = TimeService.FormatDateOnly(firstDate);
                }
                else
                {
                    barBufferDateRange = $"{TimeService.FormatDateOnly(firstDate)} to {TimeService.FormatDateOnly(lastDate)}";
                }
            }
            
            // Determine if bars exist but are from wrong trading date
            var barsFromWrongDate = false;
            var barsFromCorrectDate = false;
            if (barBufferCount > 0 && firstBarInBufferUtc.HasValue)
            {
                var firstBarDate = _time.GetChicagoDateToday(firstBarInBufferUtc.Value).ToString("yyyy-MM-dd");
                barsFromWrongDate = (firstBarDate != expectedTradingDate);
                barsFromCorrectDate = (firstBarDate == expectedTradingDate);
            }
            
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_COMPUTE_NO_BARS_DIAGNOSTIC", State.ToString(),
                new
                {
                    trading_date = TradingDate,
                    expected_trading_date = expectedTradingDate,
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    range_start_utc = RangeStartUtc.ToString("o"),
                    range_end_chicago = endTimeChicagoActual.ToString("o"),
                    range_end_utc = endTimeUtcActual.ToString("o"),
                    first_bar_timestamp_chicago = firstBarInBufferChicago?.ToString("o"),
                    first_bar_timestamp_utc = firstBarInBufferUtc?.ToString("o"),
                    last_bar_timestamp_chicago = lastBarInBufferChicago?.ToString("o"),
                    last_bar_timestamp_utc = lastBarInBufferUtc?.ToString("o"),
                    bar_buffer_count = barBufferCount,
                    bar_buffer_date_range = barBufferDateRange ?? "NO_BARS",
                    bars_from_wrong_date = barsFromWrongDate,
                    bars_from_correct_date = barsFromCorrectDate,
                    note = barsFromWrongDate 
                        ? $"No bars found in range window - bars in buffer are from different trading date ({barBufferDateRange}). Waiting for bars from {expectedTradingDate}."
                        : "No bars found in range window - waiting for bars from correct trading date or check date alignment"
                }));
            
            // Determine more specific reason code
            string reasonCode;
            if (barBufferCount == 0)
            {
                reasonCode = "NO_BARS_YET";
            }
            else if (barsFromWrongDate)
            {
                reasonCode = "BARS_FROM_WRONG_DATE";
            }
            else if (barsFromCorrectDate)
            {
                reasonCode = "OUTSIDE_RANGE_WINDOW";
            }
            else
            {
                reasonCode = "NO_BARS_IN_WINDOW";
            }
            
            return (false, null, null, null, "UNSET", 0, reasonCode, null, null, null, null);
        }

        // Sort by timestamp (should already be sorted, but ensure correctness)
        bars.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        
        // DIAGNOSTIC: Log RANGE_WINDOW_AUDIT with timestamp conversion details (only if diagnostic logs enabled)
        if (_enableDiagnosticLogs && firstBarRawUtc.HasValue && lastBarRawUtc.HasValue)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_WINDOW_AUDIT", State.ToString(),
                new
                {
                    first_bar_chicago = firstBarChicago?.ToString("o"),
                    last_bar_chicago = lastBarChicago?.ToString("o"),
                    bar_count = bars.Count,
                    conversion_method = "ConvertUtcToChicago",
                    conversion_note = "Assumes bar.TimestampUtc is UTC (as received from NinjaTrader after conversion)"
                }));
        }

        // Compute range in one pass
        decimal rangeHigh = bars[0].High;
        decimal rangeLow = bars[0].Low;
        decimal? freezeClose = null;
        DateTimeOffset? lastBarBeforeSlotUtc = null;
        DateTimeOffset? lastBarBeforeSlotChicago = null;

        foreach (var bar in bars)
        {
            rangeHigh = Math.Max(rangeHigh, bar.High);
            rangeLow = Math.Min(rangeLow, bar.Low);
            
            // Find last bar close before end time (for freeze close)
            // Use Chicago time comparison to match trading session semantics
            // OPTIMIZATION: Reuse cached Chicago time instead of converting again
            var barChicagoTime = barChicagoTimes[bar];
            if (barChicagoTime < endTimeChicagoActual)
            {
                freezeClose = bar.Close;
                lastBarBeforeSlotUtc = bar.TimestampUtc;
                lastBarBeforeSlotChicago = barChicagoTime;
            }
        }

        // Validate range was computed successfully
        if (rangeHigh < rangeLow)
        {
            return (false, null, null, null, "UNSET", bars.Count, "INVALID_RANGE_HIGH_LOW", bars[0].TimestampUtc, bars[bars.Count - 1].TimestampUtc, _time.ConvertUtcToChicago(bars[0].TimestampUtc), _time.ConvertUtcToChicago(bars[bars.Count - 1].TimestampUtc));
        }

        // Validate we have sufficient bars for reliable range computation
        if (bars.Count < 3)
        {
            return (false, null, null, null, "UNSET", bars.Count, "INSUFFICIENT_BARS", bars[0].TimestampUtc, bars[bars.Count - 1].TimestampUtc, _time.ConvertUtcToChicago(bars[0].TimestampUtc), _time.ConvertUtcToChicago(bars[bars.Count - 1].TimestampUtc));
        }

        // Validate we have a freeze close
        if (!freezeClose.HasValue || !lastBarBeforeSlotUtc.HasValue)
        {
            return (false, null, null, null, "UNSET", bars.Count, "NO_FREEZE_CLOSE", bars[0].TimestampUtc, bars[bars.Count - 1].TimestampUtc, _time.ConvertUtcToChicago(bars[0].TimestampUtc), _time.ConvertUtcToChicago(bars[bars.Count - 1].TimestampUtc));
        }

        // CRITICAL: Validate timezone edge cases (DST, holidays, early closes)
        // Timezone edge case problems:
        // - DST transitions: Missing hour (spring forward) or duplicate hour (fall back)
        // - Holidays: Early closes, missing sessions
        // - What breaks: BarsRequest window wrong, live bars appear "in future", range windows misalign
        //
        // Mitigation: Validate expected vs actual bar count and session length
        var sessionDurationMinutes = (endTimeChicagoActual - RangeStartChicagoTime).TotalMinutes;
        var expectedBarCount = (int)Math.Round(sessionDurationMinutes); // 1-minute bars
        var actualBarCount = bars.Count;
        var barCountDiff = actualBarCount - expectedBarCount;
        var barCountMismatch = Math.Abs(barCountDiff) > 5; // Allow 5 bar tolerance for gaps
        
        // Check for DST transition (offset change within session)
        var startOffset = RangeStartChicagoTime.Offset;
        var endOffset = endTimeChicagoActual.Offset;
        var dstTransitionDetected = startOffset != endOffset;
        
        // Check for session length anomaly (early close or extended session)
        var nominalSessionLengthMinutes = (SlotTimeChicagoTime - RangeStartChicagoTime).TotalMinutes;
        var actualSessionLengthMinutes = sessionDurationMinutes;
        var sessionLengthAnomaly = Math.Abs(actualSessionLengthMinutes - nominalSessionLengthMinutes) > 10; // Allow 10 min tolerance
        
        // Log timezone edge case warnings if detected
        if (barCountMismatch || dstTransitionDetected || sessionLengthAnomaly)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "TIMEZONE_EDGE_CASE_DETECTED", State.ToString(),
                new
                {
                    expected_bar_count = expectedBarCount,
                    actual_bar_count = actualBarCount,
                    bar_count_mismatch = barCountMismatch,
                    bar_count_diff = barCountDiff,
                    note_bar_count_mismatch = barCountMismatch 
                        ? "Bar count differs from expected (informational only - not an error). Possible causes: partial-minute boundaries, DST transitions, deduplication, low liquidity early closes."
                        : "Bar count matches expected (within tolerance)",
                    nominal_session_length_minutes = nominalSessionLengthMinutes,
                    actual_session_length_minutes = actualSessionLengthMinutes,
                    session_length_anomaly = sessionLengthAnomaly,
                    session_length_diff_minutes = actualSessionLengthMinutes - nominalSessionLengthMinutes,
                    dst_transition_detected = dstTransitionDetected,
                    start_offset = startOffset.ToString(),
                    end_offset = endOffset.ToString(),
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    range_end_chicago = endTimeChicagoActual.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                    note = dstTransitionDetected 
                        ? "DST transition detected - missing or duplicate hour may affect bar count"
                        : sessionLengthAnomaly
                            ? "Session length anomaly detected - possible early close or extended session"
                            : "Bar count mismatch - possible DST transition, holiday, or data gap"
                }));
        }
        
        // Return first and last bar timestamps in both UTC and Chicago time for auditability
        var firstBarUtc = bars[0].TimestampUtc;
        var lastBarUtc = bars[bars.Count - 1].TimestampUtc;
        var firstBarChicagoResult = _time.ConvertUtcToChicago(firstBarUtc);
        var lastBarChicagoResult = _time.ConvertUtcToChicago(lastBarUtc);

        return (true, rangeHigh, rangeLow, freezeClose, "BAR_CLOSE", bars.Count, null, firstBarUtc, lastBarUtc, firstBarChicagoResult, lastBarChicagoResult);
    }

    /// <summary>
    /// SINGLE AUTHORITATIVE METHOD: Finalize and lock range for this stream + trading day.
    /// This is the ONLY place ranges can be committed and RANGE_LOCKED state transition can occur.
    /// Returns true if range was already locked or successfully locked, false if locking failed.
    /// </summary>
    private bool TryLockRange(DateTimeOffset utcNow)
    {
        // Already locked - idempotent return
        if (_rangeLocked)
            return true;
        
        // GUARD: Check if BarsRequest is still pending for this instrument
        // Prevents range lock before BarsRequest completes (avoids locking with insufficient bars)
        // CRITICAL: Check both CanonicalInstrument and ExecutionInstrument
        // BarsRequest might be marked pending with either one
        if (IsSimMode() && _engine != null)
        {
            var isPending = _engine.IsBarsRequestPending(CanonicalInstrument, utcNow) ||
                           _engine.IsBarsRequestPending(ExecutionInstrument, utcNow);
            
            if (isPending)
            {
                // BarsRequest is still pending - wait for it to complete
                // Log rate-limited warning (once per minute max)
                var shouldLog = !_lastRangeComputeFailedLogUtc.HasValue || 
                               (utcNow - _lastRangeComputeFailedLogUtc.Value).TotalMinutes >= 1.0;
                
                if (shouldLog)
                {
                    _lastRangeComputeFailedLogUtc = utcNow;
                    LogHealth("WARN", "RANGE_LOCK_BLOCKED_BARSREQUEST_PENDING",
                        "Range lock blocked - BarsRequest is still pending for this instrument",
                        new
                        {
                            execution_instrument = ExecutionInstrument,
                            canonical_instrument = CanonicalInstrument,
                            canonical_pending = _engine.IsBarsRequestPending(CanonicalInstrument, utcNow),
                            execution_pending = _engine.IsBarsRequestPending(ExecutionInstrument, utcNow),
                            slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                            current_time_chicago = _time.ConvertUtcToChicago(utcNow).ToString("o"),
                            note = "Range lock will proceed once BarsRequest completes or times out (5 minutes)"
                        });
                }
                
                // Return false to retry on next tick
                return false;
            }
        }
        
        // Compute final range from all available bars
        var rangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: SlotTimeUtc);
        
        if (!rangeResult.Success)
        {
            // Log failure but don't throw - caller handles retry logic
            LogHealth("WARN", "RANGE_LOCK_FAILED", 
                $"Failed to compute final range for locking",
                new
                {
                    reason = rangeResult.Reason ?? "UNKNOWN",
                    bar_count = rangeResult.BarCount,
                    slot_time_utc = SlotTimeUtc.ToString("o"),
                    utc_now = utcNow.ToString("o")
                });
            _rangeLockAttemptedAtUtc = utcNow;
            _rangeLockFailureCount++;
            return false;
        }
        
        // VALIDATION: Ensure range was properly computed before locking
        // Check 1: Range values must be present
        if (!rangeResult.RangeHigh.HasValue || !rangeResult.RangeLow.HasValue || !rangeResult.FreezeClose.HasValue)
        {
            LogHealth("CRITICAL", "RANGE_LOCK_VALIDATION_FAILED",
                "Cannot lock range - range values are missing",
                new
                {
                    range_high_has_value = rangeResult.RangeHigh.HasValue,
                    range_low_has_value = rangeResult.RangeLow.HasValue,
                    freeze_close_has_value = rangeResult.FreezeClose.HasValue,
                    bar_count = rangeResult.BarCount,
                    reason = rangeResult.Reason
                });
            _rangeLockAttemptedAtUtc = utcNow;
            _rangeLockFailureCount++;
            return false;
        }

        // Check 2: Range high must be greater than range low (sanity check)
        if (rangeResult.RangeHigh.Value <= rangeResult.RangeLow.Value)
        {
            LogHealth("CRITICAL", "RANGE_LOCK_VALIDATION_FAILED",
                "Cannot lock range - range high is not greater than range low",
                new
                {
                    range_high = rangeResult.RangeHigh.Value,
                    range_low = rangeResult.RangeLow.Value,
                    bar_count = rangeResult.BarCount,
                    note = "Invalid range values - high must be > low"
                });
            _rangeLockAttemptedAtUtc = utcNow;
            _rangeLockFailureCount++;
            return false;
        }

        // Check 3: Must have bars in buffer (range was computed from actual data)
        if (rangeResult.BarCount == 0)
        {
            LogHealth("CRITICAL", "RANGE_LOCK_VALIDATION_FAILED",
                "Cannot lock range - no bars were used in computation",
                new
                {
                    bar_count = rangeResult.BarCount,
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                    note = "Range computation returned 0 bars - cannot lock without data"
                });
            _rangeLockAttemptedAtUtc = utcNow;
            _rangeLockFailureCount++;
            return false;
        }

        // All validation passed - log validation success for auditability
        LogHealth("INFO", "RANGE_LOCK_VALIDATION_PASSED",
            "Range validation passed - proceeding with lock",
            new
            {
                range_high = rangeResult.RangeHigh.Value,
                range_low = rangeResult.RangeLow.Value,
                freeze_close = rangeResult.FreezeClose.Value,
                bar_count = rangeResult.BarCount,
                first_bar_chicago = rangeResult.FirstBarChicago?.ToString("o"),
                last_bar_chicago = rangeResult.LastBarChicago?.ToString("o")
            });
        
        // CRITICAL: Atomic commit - set all values together
        RangeHigh = rangeResult.RangeHigh;
        RangeLow = rangeResult.RangeLow;
        FreezeClose = rangeResult.FreezeClose;
        FreezeCloseSource = rangeResult.FreezeCloseSource;
        
        // ============================================================
        // PHASE A: ATOMIC LOCK (no side effects)
        // ============================================================
        
        // 1. ComputeRangeRetrospectively(endTimeUtc = SlotTimeUtc)
        // (Already computed above)
        
        // 2. Commit RangeHigh/Low/FreezeClose/Source
        // (Already committed above)
        
        // 3. Attempt breakout derivation
        ComputeBreakoutLevelsAndLog(utcNow);
        
        // REQUIRED CHANGE #3: Do NOT block locking if derivation fails
        // Remove: "If breakout levels missing => return false"
        // Replace with: Lock the range anyway, log RANGE_LOCKED_DERIVATION_FAILED, apply trading gate
        // Otherwise a rounding/tick bug can prevent locking forever.
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
        {
            // Set gate flag to prevent entries until breakout levels exist
            _breakoutLevelsMissing = true;
            
            LogHealth("WARN", "RANGE_LOCKED_DERIVATION_FAILED", 
                "Range locked successfully but breakout levels not computed - stream blocked from entry until resolved",
                new
                {
                    range_high = RangeHigh,
                    range_low = RangeLow,
                    brk_long_has_value = _brkLongRounded.HasValue,
                    brk_short_has_value = _brkShortRounded.HasValue,
                    gate_flag_set = _breakoutLevelsMissing,
                    note = "Range lock succeeded; breakout levels are derived and will be enforced as separate trading gate"
                });
        }
        else
        {
            // Breakout levels computed successfully - clear gate flag
            _breakoutLevelsMissing = false;
        }
        
        // 4. Set _rangeLocked = true
        // CRITICAL: This is the point of no return - after this, range is immutable
        _rangeLocked = true;
        
        // 5. Transition to RANGE_LOCKED
        // CRITICAL: This completes Phase A - range is now locked and state is RANGE_LOCKED
        var rangeLogData = CreateRangeLogData(RangeHigh, RangeLow, FreezeClose, FreezeCloseSource);
        Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED", rangeLogData);
        
        // Mark lock as committed (for duplicate detection)
        _rangeLockCommitted = true;
        
        // Reset failure count on success
        _rangeLockFailureCount = 0;
        
        // ============================================================
        // PHASE B: BEST-EFFORT SIDE EFFECTS (failures don't affect lock)
        // ============================================================
        // REQUIRED CHANGE #2: Phase B must be wrapped so failures cannot strand the stream in a half-locked state.
        
        try
        {
            // 1. EmitRangeLockedEvents
            EmitRangeLockedEvents(utcNow, rangeResult);
            
            // 2. SLOT_END_SUMMARY is emitted from SubmitStopEntryBracketsAtLock after submit (truth-bearing:
            //    brackets submitted vs failed). Pre-submit "awaiting signal" was misleading when submit failed.
            
            // SIMPLIFICATION: Removed immediate entry path - always use stop brackets
            // Stop brackets handle all entry scenarios:
            // - If price already beyond breakout → stop fills immediately (marketable stop behavior)
            // - If price not at breakout → stop waits for breakout
            // This eliminates race conditions and simplifies execution to single entry mechanism
            //
            // Gate: Ensure breakout levels exist (canonical prerequisite for stop bracket submission)
            // Note: SubmitStopEntryBracketsAtLock() has internal gates on _brkLongRounded/_brkShortRounded (line 3255)
            //       and _breakoutLevelsMissing (line 3235), so this gate is redundant but kept for clarity
            //
            // INITIAL SUBMISSION FRESHNESS: Block materially delayed first submissions.
            // Normal immediate slot-time: within freshness window → allow (marketable stop OK).
            // Delayed initial: beyond window → block NO_TRADE_MATERIALLY_DELAYED_INITIAL_SUBMISSION.
            // freshness_minutes <= 0 disables this gate (parity spec); price sanity below still applies.
            // Delayed resubmit/restart retry: same unified validity as first lock (HandleRangeLockedState + recovery).
            var freshnessMinutes = _spec?.breakout?.initial_submission_freshness_minutes ?? 0;
            var delayFromSlotMinutes = (utcNow - SlotTimeUtc).TotalMinutes;
            if (freshnessMinutes > 0 && delayFromSlotMinutes > freshnessMinutes)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "INITIAL_SUBMISSION_BLOCKED_MATERIALLY_DELAYED", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        metric_type = "blocked_freshness",
                        delay_from_slot_minutes = Math.Round(delayFromSlotMinutes, 2),
                        freshness_window_minutes = freshnessMinutes,
                        slot_time_utc = SlotTimeUtc.ToString("o"),
                        note = "Initial submission blocked - materially delayed from slot time; would create late entry"
                    }));
                _ = Commit(utcNow, "NO_TRADE_MATERIALLY_DELAYED_INITIAL_SUBMISSION", "NO_TRADE_MATERIALLY_DELAYED_INITIAL_SUBMISSION");
            }
            else if (utcNow < MarketCloseUtc && !_breakoutLevelsMissing && _brkLongRounded.HasValue && _brkShortRounded.HasValue)
            {
                // Pre-submit: OHLC strictly after slot is authoritative; journal flags are optional memory (e.g. restart).
                if (!TryCommitNoTradeIfPostLockBreakoutDetected(utcNow, "TRY_LOCK_RANGE_PHASE_B"))
                {
                    var (bid, ask) = _executionAdapter?.GetCurrentMarketPrice(ExecutionInstrument, utcNow) ?? (null, null);
                    var brkLong = _brkLongRounded.Value;
                    var brkShort = _brkShortRounded.Value;
                    var delayMin = (utcNow - SlotTimeUtc).TotalMinutes;
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "EXECUTION_METRIC_INITIAL_SUBMISSION_ALLOWED", State.ToString(),
                        new
                        {
                            stream_id = Stream,
                            metric_type = "initial_submission_allowed",
                            delay_from_slot_minutes = Math.Round(delayMin, 3),
                            current_bid = bid,
                            current_ask = ask,
                            brk_long = _brkLongRounded,
                            brk_short = _brkShortRounded,
                            price_distance_long_ticks = ask.HasValue && _tickSize > 0 ? (ask.Value - brkLong) / _tickSize : (decimal?)null,
                            price_distance_short_ticks = bid.HasValue && _tickSize > 0 ? (brkShort - bid.Value) / _tickSize : (decimal?)null
                        }));
                    SubmitStopEntryBracketsAtLock(utcNow);
                }
            }
            else if (_breakoutLevelsMissing)
            {
                // Log why brackets weren't submitted
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "STOP_BRACKETS_BLOCKED_MISSING_BREAKOUTS", State.ToString(),
                    new
                    {
                        reason = "BREAKOUT_LEVELS_MISSING",
                        gate_flag = _breakoutLevelsMissing,
                        note = "Brackets blocked because breakout levels failed to compute - stream gated from entry"
                    }));
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - range is locked, post-lock actions are best-effort
            // CRITICAL: Do NOT reset _rangeLocked or _rangeLockCommitted - lock is valid
            // Phase B failures cannot strand the stream in a half-locked state
            LogHealth("ERROR", "RANGE_LOCKED_POST_ACTIONS_FAILED", 
                "Failed to execute post-lock actions (range is still locked)",
                new 
                { 
                    error = ex.Message,
                    range_locked = _rangeLocked,
                    range_committed = _rangeLockCommitted,
                    note = "Lock is valid; post-lock side effects failed but don't affect lock invariant"
                });
        }
        
        return true;
    }

    /// <summary>
    /// Emit all RANGE_LOCKED events (RangeLockedEvent and HydrationEvent).
    /// Called ONLY from TryLockRange to ensure single emission point.
    /// </summary>
    private void EmitRangeLockedEvents(DateTimeOffset utcNow, (bool Success, decimal? RangeHigh, decimal? RangeLow, decimal? FreezeClose, string FreezeCloseSource, int BarCount, string? Reason, DateTimeOffset? FirstBarUtc, DateTimeOffset? LastBarUtc, DateTimeOffset? FirstBarChicago, DateTimeOffset? LastBarChicago) rangeResult)
    {
        if (!RangeHigh.HasValue || !RangeLow.HasValue || !FreezeClose.HasValue)
            return;
        
        try
        {
            // Create and persist canonical RANGE_LOCKED event
            var rangeLockedEvent = new RangeLockedEvent(
                tradingDay: TradingDate,
                streamId: Stream,
                canonicalInstrument: CanonicalInstrument,
                executionInstrument: ExecutionInstrument,
                rangeHigh: RangeHigh.Value,
                rangeLow: RangeLow.Value,
                rangeSize: RangeHigh.Value - RangeLow.Value,
                freezeClose: FreezeClose.Value,
                rangeHighRounded: RangeHigh.Value,
                rangeLowRounded: RangeLow.Value,
                breakoutLong: _brkLongRounded ?? 0m, // Use 0 if not computed (persister will handle)
                breakoutShort: _brkShortRounded ?? 0m,
                rangeStartTimeChicago: RangeStartChicagoTime.ToString("o"),
                rangeStartTimeUtc: RangeStartUtc.ToString("o"),
                rangeEndTimeChicago: SlotTimeChicagoTime.ToString("o"),
                rangeEndTimeUtc: SlotTimeUtc.ToString("o"),
                lockedAtChicago: _time.ConvertUtcToChicago(utcNow).ToString("o"),
                lockedAtUtc: utcNow.ToString("o")
            );
            
            _rangePersister?.Persist(rangeLockedEvent);
            
            // Also log to hydration file
            var chicagoNow = _time.ConvertUtcToChicago(utcNow);
            var barCount = GetBarBufferCount();
            var rangeLockedData = new Dictionary<string, object>
            {
                ["range_high"] = RangeHigh.Value,
                ["range_low"] = RangeLow.Value,
                ["range_size"] = RangeHigh.Value - RangeLow.Value,
                ["freeze_close"] = FreezeClose.Value,
                ["breakout_long"] = _brkLongRounded.HasValue ? _brkLongRounded.Value : (decimal?)null,
                ["breakout_short"] = _brkShortRounded.HasValue ? _brkShortRounded.Value : (decimal?)null,
                ["range_start_time_chicago"] = RangeStartChicagoTime.ToString("o"),
                ["range_end_time_chicago"] = SlotTimeChicagoTime.ToString("o"),
                ["range_start_time_utc"] = RangeStartUtc.ToString("o"),
                ["range_end_time_utc"] = SlotTimeUtc.ToString("o"),
                ["bar_count"] = barCount,
                ["tick_size"] = _tickSize,
                ["source"] = "final",
                ["breakout_levels_missing"] = _breakoutLevelsMissing
            };
            
            var hydrationEvent = new HydrationEvent(
                eventType: "RANGE_LOCKED",
                tradingDay: TradingDate,
                streamId: Stream,
                canonicalInstrument: CanonicalInstrument,
                executionInstrument: ExecutionInstrument,
                session: Session,
                slotTimeChicago: SlotTimeChicago,
                timestampUtc: utcNow,
                timestampChicago: chicagoNow,
                state: State.ToString(),
                data: rangeLockedData
            );
            
            _hydrationPersister?.Persist(hydrationEvent);
            
            // HARD ASSERTION: Check for duplicate RANGE_LOCKED events
            // This should never happen if TryLockRange is the only entry point
            if (_rangeLockAssertEmitted)
            {
                LogHealth("CRITICAL", "DUPLICATE_RANGE_LOCKED", 
                    "RANGE_LOCKED event emitted more than once per stream per trading day - CRITICAL ERROR",
                    new
                    {
                        stream_id = Stream,
                        trading_date = TradingDate,
                        violation = "DUPLICATE_RANGE_LOCKED_EVENT",
                        note = "This indicates a logic bug - TryLockRange should be the only entry point"
                    });
            }
            _rangeLockAssertEmitted = true;
        }
        catch (Exception ex)
        {
            LogHealth("ERROR", "RANGE_LOCKED_EVENT_EMIT_FAILED", 
                "Failed to emit RANGE_LOCKED events",
                new { error = ex.Message, note = "Range is locked but events failed - execution continues" });
        }
    }

    /// <summary>
    /// Restore locked range state from canonical hydration/ranges log.
    /// REQUIRED CHANGE #4: On startup, replay hydration_{day}.jsonl (or ranges_{day}.jsonl).
    /// If a RANGE_LOCKED event exists for this stream+day, restore locked state.
    /// </summary>
    private void RestoreRangeLockedFromHydrationLog(string tradingDay, string streamId)
    {
        try
        {
            // Try hydration log first (new layout), then legacy logs/robot, then ranges (new + legacy).
            var hydrationPreferred = Path.Combine(RobotRunArtifactPaths.LogsHydration(_robotStateRoot), $"hydration_{tradingDay}.jsonl");
            var hydrationLegacy = Path.Combine(RobotRunArtifactPaths.LogsRobot(_robotStateRoot), $"hydration_{tradingDay}.jsonl");
            var rangesPreferred = Path.Combine(RobotRunArtifactPaths.LogsRanges(_robotStateRoot), $"ranges_{tradingDay}.jsonl");
            var rangesLegacy = Path.Combine(RobotRunArtifactPaths.LogsRobot(_robotStateRoot), $"ranges_{tradingDay}.jsonl");

            var hydrationFile = File.Exists(hydrationPreferred) ? hydrationPreferred
                : (File.Exists(hydrationLegacy) ? hydrationLegacy : hydrationPreferred);
            var usingRangesFile = false;
            if (!File.Exists(hydrationFile))
            {
                hydrationFile = File.Exists(rangesPreferred) ? rangesPreferred
                    : (File.Exists(rangesLegacy) ? rangesLegacy : rangesPreferred);
                usingRangesFile = true;
                if (!File.Exists(hydrationFile))
                {
                    LogHealth("WARN", "RANGE_LOCKED_RESTORE_FILE_MISSING", 
                        "Neither hydration nor ranges log file found for restoration",
                        new
                        {
                            trading_day = tradingDay,
                            stream_id = streamId,
                            hydration_file = hydrationPreferred,
                            ranges_file = rangesPreferred,
                            note = "Will proceed without restoration - range will be recomputed"
                        });
                    return;
                }
            }
            
            // Diagnostic: Log which file we're using
            LogHealth("INFO", "RANGE_LOCKED_RESTORE_ATTEMPT", 
                "Attempting to restore range lock from log file",
                new
                {
                    trading_day = tradingDay,
                    stream_id = streamId,
                    file_path = hydrationFile,
                    file_type = usingRangesFile ? "ranges" : "hydration",
                    file_exists = File.Exists(hydrationFile)
                });
            
            // Read hydration/ranges log and find RANGE_LOCKED event for this stream
            // IMPORTANT: Find the MOST RECENT event (last one in file), not the first
            var lines = File.ReadAllLines(hydrationFile);
            Dictionary<string, object>? latestHydrationData = null; // Store data dict directly (avoids HydrationEvent deserialization issues)
            RangeLockedEvent? latestRangeEvt = null;
            
            // Scan all lines to find the most recent matching event
            // CRITICAL: Check JSON structure first to determine which type to deserialize
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                try
                {
                    // Quick check: Does this line contain RANGE_LOCKED event for our stream?
                    // This avoids expensive deserialization attempts on every line
                    if (!line.Contains($"\"stream_id\":\"{streamId}\"") || 
                        !line.Contains($"\"trading_day\":\"{tradingDay}\"") ||
                        !line.Contains("RANGE_LOCKED"))
                    {
                        continue; // Skip lines that can't possibly match
                    }
                    
                    // Determine format: hydration log has "data" dictionary, ranges log has flat structure
                    bool looksLikeHydrationFormat = line.Contains("\"data\":{");
                    bool looksLikeRangesFormat = line.Contains("\"range_high\":") && !line.Contains("\"data\":");
                    
                    // Try HydrationEvent first (hydration log format)
                    // CRITICAL: HydrationEvent has constructor-only properties, so deserialization might fail
                    // Instead, parse JSON manually to extract fields
                    if (looksLikeHydrationFormat)
                    {
                        try
                        {
                            // Parse as dictionary first to avoid constructor-only property issues
                            var dict = JsonUtil.Deserialize<Dictionary<string, object>>(line);
                            if (dict != null &&
                                dict.TryGetValue("event_type", out var evtType) && evtType?.ToString() == "RANGE_LOCKED" &&
                                dict.TryGetValue("trading_day", out var td) && td?.ToString() == tradingDay &&
                                dict.TryGetValue("stream_id", out var sid) && sid?.ToString() == streamId &&
                                dict.TryGetValue("slot_time_chicago", out var stc) && stc?.ToString() == SlotTimeChicago)
                            {
                                // Extract data dictionary - handle different deserializer types
                                Dictionary<string, object>? dataDict = null;
                                if (dict.TryGetValue("data", out var dataObj))
                                {
                                    // JavaScriptSerializer returns Dictionary<string, object> directly
                                    if (dataObj is Dictionary<string, object> directDict)
                                    {
                                        dataDict = directDict;
                                    }
                                    // System.Text.Json might wrap it differently - try to convert
                                    else if (dataObj != null)
                                    {
                                        // Try to convert to Dictionary<string, object>
                                        try
                                        {
                                            var jsonStr = JsonUtil.Serialize(dataObj);
                                            dataDict = JsonUtil.Deserialize<Dictionary<string, object>>(jsonStr);
                                        }
                                        catch (Exception convertEx)
                                        {
                                            // Log conversion failure for debugging (only first few times)
                                            if (lines.Length < 100)
                                            {
                                                LogHealth("DEBUG", "RANGE_LOCKED_RESTORE_DATA_CONVERT_FAILED", 
                                                    "Failed to convert data object to dictionary",
                                                    new
                                                    {
                                                        trading_day = tradingDay,
                                                        stream_id = streamId,
                                                        data_obj_type = dataObj.GetType().Name,
                                                        error = convertEx.Message
                                                    });
                                            }
                                            // Conversion failed, skip
                                        }
                                    }
                                }
                                
                                if (dataDict != null)
                                {
                                    // Store data dictionary directly - we'll extract values from it during restoration
                                    latestHydrationData = dataDict;
                                }
                                else if (lines.Length < 100)
                                {
                                    // Log why we didn't find data dict (only for small files during testing)
                                    LogHealth("DEBUG", "RANGE_LOCKED_RESTORE_DATA_MISSING", 
                                        "Data dictionary not found or not extractable",
                                        new
                                        {
                                            trading_day = tradingDay,
                                            stream_id = streamId,
                                            has_data_obj = dict.TryGetValue("data", out _),
                                            data_obj_type = dict.TryGetValue("data", out var dobj) ? dobj?.GetType().Name : "null"
                                        });
                                }
                            }
                        }
                        catch
                        {
                            // Deserialization failed - might be malformed, skip
                        }
                    }
                    
                    // Try RangeLockedEvent (from ranges file format)
                    if (looksLikeRangesFormat)
                    {
                        try
                        {
                            var rangeEvt = JsonUtil.Deserialize<RangeLockedEvent>(line);
                            if (rangeEvt != null && 
                                rangeEvt.trading_day == tradingDay && 
                                rangeEvt.stream_id == streamId)
                                // Note: RangeLockedEvent doesn't have slot_time_chicago field, so we can't match by slot
                                // This is a limitation - ranges log format doesn't include slot info
                                // For now, we'll match by stream_id only for ranges log
                                // Hydration log matching above includes slot_time_chicago check
                            {
                                // Keep the most recent one (last in file)
                                latestRangeEvt = rangeEvt;
                            }
                        }
                        catch
                        {
                            // Deserialization failed - skip this line
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Skip malformed lines - log first few failures for debugging
                    // (Don't spam logs with every malformed line)
                    if (lines.Length < 100) // Only log if file is small (likely during testing)
                    {
                        LogHealth("DEBUG", "RANGE_LOCKED_RESTORE_DESERIALIZE_FAILED", 
                            "Failed to deserialize line from log file",
                            new
                            {
                                trading_day = tradingDay,
                                stream_id = streamId,
                                line_preview = line.Length > 100 ? line.Substring(0, 100) + "..." : line,
                                error = ex.Message
                            });
                    }
                    continue;
                }
            }
            
            // Diagnostic: Log what we found
            LogHealth("INFO", "RANGE_LOCKED_RESTORE_SCAN_COMPLETE", 
                "Finished scanning log file for RANGE_LOCKED events",
                new
                {
                    trading_day = tradingDay,
                    stream_id = streamId,
                    file_path = hydrationFile,
                    total_lines_scanned = lines.Length,
                    hydration_events_found = latestHydrationData != null ? 1 : 0,
                    range_events_found = latestRangeEvt != null ? 1 : 0,
                    will_restore = latestHydrationData != null || latestRangeEvt != null
                });
            
            // Restore from the most recent event found (prefer hydration log over ranges log)
            if (latestHydrationData != null)
            {
                // Helper function to extract decimal from dictionary value (handles JsonElement from System.Text.Json)
                decimal? ExtractDecimal(Dictionary<string, object> dict, string key)
                {
                    if (!dict.ContainsKey(key)) return null;
                    var value = dict[key];
                    if (value == null) return null;
                    
                    // Handle JsonElement (from System.Text.Json)
                    var jsonElementType = System.Type.GetType("System.Text.Json.JsonElement, System.Text.Json");
                    if (jsonElementType != null && jsonElementType.IsInstanceOfType(value))
                    {
                        try
                        {
                            var getDecimalMethod = jsonElementType.GetMethod("GetDecimal");
                            if (getDecimalMethod != null)
                            {
                                return (decimal?)getDecimalMethod.Invoke(value, null);
                            }
                        }
                        catch
                        {
                            // Fall through to Convert.ToDecimal
                        }
                    }
                    
                    // Handle direct decimal or string conversion
                    try
                    {
                        return Convert.ToDecimal(value);
                    }
                    catch
                    {
                        return null;
                    }
                }
                
                // Restore locked state from canonical hydration log (extract from data dictionary)
                _rangeLocked = true;
                // CRITICAL: Broker orders are lost on process restart. Force resubmit of stop-entry brackets.
                _stopBracketsSubmittedAtLock = false;
                LogHealth("INFO", "RANGE_LOCKED_RESTORE_RESUBMIT_BRACKETS",
                    "Reset StopBracketsSubmittedAtLock for resubmit - broker orders lost on restart",
                    new { trading_day = tradingDay, stream_id = streamId });
                RangeHigh = ExtractDecimal(latestHydrationData, "range_high");
                RangeLow = ExtractDecimal(latestHydrationData, "range_low");
                FreezeClose = ExtractDecimal(latestHydrationData, "freeze_close");
                
                // Restore breakout levels if present
                _brkLongRounded = ExtractDecimal(latestHydrationData, "breakout_long");
                _brkShortRounded = ExtractDecimal(latestHydrationData, "breakout_short");
                
                // Set gate flag if breakout levels are missing
                _breakoutLevelsMissing = !_brkLongRounded.HasValue || !_brkShortRounded.HasValue;
                
                // Mark lock as committed (for duplicate detection)
                _rangeLockCommitted = true;
                
                // Restore state as RANGE_LOCKED
                var utcNow = DateTimeOffset.UtcNow;
                
                // If breakout levels are missing but range is available, compute them
                if ((!_brkLongRounded.HasValue || !_brkShortRounded.HasValue) && 
                    RangeHigh.HasValue && RangeLow.HasValue)
                {
                    ComputeBreakoutLevelsAndLog(utcNow);
                }
                var rangeLogData = CreateRangeLogData(RangeHigh, RangeLow, FreezeClose, FreezeCloseSource);
                Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED_RESTORED", rangeLogData);
                
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "RANGE_LOCKED_RESTORED_FROM_HYDRATION", State.ToString(),
                    new
                    {
                        trading_date = tradingDay,
                        stream_id = streamId,
                        range_high = RangeHigh,
                        range_low = RangeLow,
                        source = "hydration_log",
                        note = "Range lock restored from canonical hydration log"
                    }));
                
                return; // Found and restored
            }
            else if (latestRangeEvt != null)
            {
                // Restore locked state from canonical ranges log
                _rangeLocked = true;
                // CRITICAL: Broker orders are lost on process restart. Force resubmit of stop-entry brackets.
                _stopBracketsSubmittedAtLock = false;
                LogHealth("INFO", "RANGE_LOCKED_RESTORE_RESUBMIT_BRACKETS",
                    "Reset StopBracketsSubmittedAtLock for resubmit - broker orders lost on restart",
                    new { trading_day = tradingDay, stream_id = streamId });
                RangeHigh = latestRangeEvt.range_high;
                RangeLow = latestRangeEvt.range_low;
                FreezeClose = latestRangeEvt.freeze_close;
                FreezeCloseSource = "RESTORED";
                
                // Restore breakout levels
                _brkLongRounded = latestRangeEvt.breakout_long;
                _brkShortRounded = latestRangeEvt.breakout_short;
                
                // Set gate flag if breakout levels are missing
                _breakoutLevelsMissing = !_brkLongRounded.HasValue || !_brkShortRounded.HasValue;
                
                // Mark lock as committed (for duplicate detection)
                _rangeLockCommitted = true;
                
                // Restore state as RANGE_LOCKED
                var utcNow = DateTimeOffset.UtcNow;
                
                // If breakout levels are missing but range is available, compute them
                if ((!_brkLongRounded.HasValue || !_brkShortRounded.HasValue) && 
                    RangeHigh.HasValue && RangeLow.HasValue)
                {
                    ComputeBreakoutLevelsAndLog(utcNow);
                }
                var rangeLogData = CreateRangeLogData(RangeHigh, RangeLow, FreezeClose, FreezeCloseSource);
                Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED_RESTORED", rangeLogData);
                
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "RANGE_LOCKED_RESTORED_FROM_RANGES", State.ToString(),
                    new
                    {
                        trading_date = tradingDay,
                        stream_id = streamId,
                        range_high = RangeHigh,
                        range_low = RangeLow,
                        source = "ranges_log",
                        note = "Range lock restored from canonical ranges log"
                    }));
                
                return; // Found and restored
            }
            else
            {
                // No events found
                LogHealth("WARN", "RANGE_LOCKED_RESTORE_NO_EVENTS", 
                    "No RANGE_LOCKED events found in log file for this stream",
                    new
                    {
                        trading_day = tradingDay,
                        stream_id = streamId,
                        file_path = hydrationFile,
                        note = "Will proceed without restoration - range will be recomputed"
                    });
            }
        }
        catch (Exception ex)
        {
            LogHealth("WARN", "RANGE_LOCKED_RESTORE_FAILED", 
                "Failed to restore range lock from hydration/ranges log",
                new 
                { 
                    trading_day = tradingDay,
                    stream_id = streamId,
                    error = ex.Message,
                    error_type = ex.GetType().Name,
                    stack_trace = ex.StackTrace,
                    note = "Will proceed without restoration" 
                });
        }
    }

    /// <summary>
    /// Restore partial RANGE_BUILDING state from persisted snapshot.
    /// Called when previous state was RANGE_BUILDING and RANGE_LOCKED restore did not apply.
    /// </summary>
    private void RestoreRangeBuildingFromSnapshot(string tradingDay, string streamId)
    {
        var utcNow = DateTimeOffset.UtcNow;
        if (_rangeBuildingSnapshotPersister == null)
        {
            LogHealth("CRITICAL", "RANGE_BUILDING_RESTORE_FALLBACK_TO_EMPTY",
                "RANGE_BUILDING snapshot restore skipped - persister not available",
                new { stream_id = streamId, trading_date = tradingDay, reason = "PERSISTER_NULL" });
            FallbackToRebuild(tradingDay, streamId, utcNow, "PERSISTER_NULL", null);
            return;
        }

        // Log path info at read time (detect NT vs module path mismatch)
        var pathInfo = _rangeBuildingSnapshotPersister.GetPathInfo(tradingDay);
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "RANGE_BUILDING_SNAPSHOT_PATH_INFO", "RESTORE_READ",
            new
            {
                resolved_project_root = pathInfo.resolved_project_root,
                absolute_snapshot_path = pathInfo.absolute_snapshot_path,
                note = "read_time"
            }));

        var snapshot = _rangeBuildingSnapshotPersister.LoadLatest(tradingDay, streamId);
        if (snapshot == null)
        {
            var diag = _rangeBuildingSnapshotPersister.GetRestoreDiagnostics(tradingDay, streamId);
            var failureReason = !diag.file_exists ? "FILE_NOT_FOUND"
                : diag.total_lines == 0 ? "EMPTY_FILE"
                : diag.stream_line_count == 0 ? "STREAM_NOT_FOUND"
                : "PARSE_ERROR";
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_BUILDING_SNAPSHOT_RESTORE_FAILED_DIAGNOSTIC", "RESTORE_FAILED",
                new
                {
                    stream = streamId,
                    trading_date = tradingDay,
                    snapshot_file_path = diag.file_path,
                    project_root = pathInfo.resolved_project_root,
                    file_exists = diag.file_exists,
                    total_lines_in_file = diag.total_lines,
                    matching_lines_for_stream = diag.stream_line_count,
                    failure_reason = failureReason
                }));
            LogHealth("CRITICAL", "RANGE_BUILDING_SNAPSHOT_RESTORE_FAILED",
                "Previous state was RANGE_BUILDING but no valid snapshot found - falling back to rebuild",
                new
                {
                    stream_id = streamId,
                    trading_date = tradingDay,
                    slot_time = SlotTimeChicago,
                    reason = failureReason,
                    snapshot_file_path_read = diag.file_path,
                    file_exists_at_restart = diag.file_exists,
                    total_lines_in_file = diag.total_lines,
                    stream_line_count = diag.stream_line_count,
                    bar_count = 0,
                    range_high = (decimal?)null,
                    range_low = (decimal?)null,
                    last_processed_bar_time = (string?)null
                });
            FallbackToRebuild(tradingDay, streamId, utcNow, failureReason, diag);
            return;
        }

        // Relaxed identity: only require stream, instrument, trading_date. Do NOT invalidate for slot_time/session.
        var coreMatch = snapshot.trading_date == tradingDay &&
            snapshot.stream_id == streamId &&
            string.Equals(snapshot.instrument ?? "", CanonicalInstrument ?? "", StringComparison.OrdinalIgnoreCase);
        if (!coreMatch)
        {
            var diag = _rangeBuildingSnapshotPersister.GetRestoreDiagnostics(tradingDay, streamId);
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_BUILDING_SNAPSHOT_RESTORE_FAILED_DIAGNOSTIC", "RESTORE_FAILED",
                new
                {
                    stream = streamId,
                    trading_date = tradingDay,
                    snapshot_file_path = diag.file_path,
                    project_root = pathInfo.resolved_project_root,
                    file_exists = diag.file_exists,
                    total_lines_in_file = diag.total_lines,
                    matching_lines_for_stream = diag.stream_line_count,
                    failure_reason = "IDENTITY_MISMATCH"
                }));
            LogHealth("CRITICAL", "RANGE_BUILDING_RESTORE_IDENTITY_MISMATCH",
                "RANGE_BUILDING snapshot identity does not match (stream/instrument/date) - rejecting restore",
                new
                {
                    stream_id = streamId,
                    trading_date = tradingDay,
                    slot_time = SlotTimeChicago,
                    reason = "IDENTITY_MISMATCH",
                    snapshot_file_path_read = diag.file_path,
                    file_exists_at_restart = diag.file_exists,
                    total_lines_in_file = diag.total_lines,
                    stream_line_count = diag.stream_line_count,
                    snapshot_trading_date = snapshot.trading_date,
                    snapshot_stream_id = snapshot.stream_id,
                    snapshot_instrument = snapshot.instrument,
                    snapshot_session = snapshot.session,
                    snapshot_slot_time = snapshot.slot_time,
                    current_instrument = CanonicalInstrument,
                    current_session = Session,
                    bar_count = snapshot.bar_count,
                    range_high = snapshot.range_high,
                    range_low = snapshot.range_low
                });
            FallbackToRebuild(tradingDay, streamId, utcNow, "IDENTITY_MISMATCH", diag);
            return;
        }

        // Log soft mismatch if session/slot changed (we still restore)
        if (snapshot.session != Session || snapshot.slot_time != SlotTimeChicago)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_BUILDING_RESTORE_IDENTITY_SOFT_MISMATCH", State.ToString(),
                new
                {
                    stream_id = streamId,
                    trading_date = tradingDay,
                    snapshot_session = snapshot.session,
                    snapshot_slot_time = snapshot.slot_time,
                    current_session = Session,
                    current_slot_time = SlotTimeChicago,
                    note = "Restoring anyway - slot_time/session change does not invalidate"
                }));
        }

        // Validate timestamp sanity
        if (!DateTimeOffset.TryParse(snapshot.last_processed_bar_time_utc, out var lastBarUtc))
        {
            var diag = _rangeBuildingSnapshotPersister.GetRestoreDiagnostics(tradingDay, streamId);
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_BUILDING_SNAPSHOT_RESTORE_FAILED_DIAGNOSTIC", "RESTORE_FAILED",
                new
                {
                    stream = streamId,
                    trading_date = tradingDay,
                    snapshot_file_path = diag.file_path,
                    project_root = pathInfo.resolved_project_root,
                    file_exists = diag.file_exists,
                    total_lines_in_file = diag.total_lines,
                    matching_lines_for_stream = diag.stream_line_count,
                    failure_reason = "PARSE_ERROR"
                }));
            LogHealth("CRITICAL", "RANGE_BUILDING_SNAPSHOT_RESTORE_FAILED",
                "Snapshot last_processed_bar_time invalid - rejecting restore",
                new
                {
                    stream_id = streamId,
                    trading_date = tradingDay,
                    reason = "INVALID_LAST_BAR_TIME",
                    snapshot_file_path_read = diag.file_path,
                    file_exists_at_restart = diag.file_exists,
                    total_lines_in_file = diag.total_lines,
                    stream_line_count = diag.stream_line_count
                });
            FallbackToRebuild(tradingDay, streamId, utcNow, "PARSE_ERROR", diag);
            return;
        }

        if (lastBarUtc > utcNow)
        {
            var diag = _rangeBuildingSnapshotPersister.GetRestoreDiagnostics(tradingDay, streamId);
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "RANGE_BUILDING_SNAPSHOT_RESTORE_FAILED_DIAGNOSTIC", "RESTORE_FAILED",
                new
                {
                    stream = streamId,
                    trading_date = tradingDay,
                    snapshot_file_path = diag.file_path,
                    project_root = pathInfo.resolved_project_root,
                    file_exists = diag.file_exists,
                    total_lines_in_file = diag.total_lines,
                    matching_lines_for_stream = diag.stream_line_count,
                    failure_reason = "PARSE_ERROR"
                }));
            LogHealth("CRITICAL", "RANGE_BUILDING_SNAPSHOT_RESTORE_FAILED",
                "Snapshot last_processed_bar_time is in the future - rejecting restore",
                new
                {
                    stream_id = streamId,
                    trading_date = tradingDay,
                    reason = "LAST_BAR_IN_FUTURE",
                    snapshot_file_path_read = diag.file_path,
                    file_exists_at_restart = diag.file_exists,
                    total_lines_in_file = diag.total_lines,
                    stream_line_count = diag.stream_line_count
                });
            FallbackToRebuild(tradingDay, streamId, utcNow, "LAST_BAR_IN_FUTURE", diag);
            return;
        }

        // Restore succeeded - bars into buffer
        lock (_barBufferLock)
        {
            _barBuffer.Clear();
            DateTimeOffset? lastBarChicago = null;
            foreach (var b in snapshot.bars ?? new List<RangeBuildingSnapshotBar>())
            {
                if (string.IsNullOrEmpty(b.timestamp_utc) || !DateTimeOffset.TryParse(b.timestamp_utc, out var barUtc))
                    continue;
                _barBuffer.Add(new Bar(barUtc, b.open, b.high, b.low, b.close, null));
                lastBarChicago = _time.ConvertUtcToChicago(barUtc);
            }
            _barBuffer.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
            if (lastBarChicago.HasValue)
                _lastBarOpenChicago = lastBarChicago;
        }

        // Restore range state
        RangeHigh = snapshot.range_high;
        RangeLow = snapshot.range_low;
        FreezeClose = snapshot.freeze_close;
        FreezeCloseSource = snapshot.freeze_close_source ?? "RESTORED";
        _lastProcessedBarTimeUtc = lastBarUtc;
        _restoredFromRangeBuildingSnapshot = true;

        State = StreamState.RANGE_BUILDING;
        _preHydrationComplete = true;

        LogHealth("INFO", "RANGE_BUILDING_SNAPSHOT_RESTORED",
            "RANGE_BUILDING state restored from snapshot - resuming from partial state",
            new
            {
                stream_id = streamId,
                trading_date = tradingDay,
                slot_time = SlotTimeChicago,
                bar_count = snapshot.bar_count,
                range_high = snapshot.range_high,
                range_low = snapshot.range_low,
                last_processed_bar_time = snapshot.last_processed_bar_time_utc,
                note = "Will continue building; bars with timestamp <= last_processed_bar_time will be deduplicated"
            });

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "RANGE_BUILDING_SNAPSHOT_RESTORED", State.ToString(),
            new
            {
                stream_id = streamId,
                trading_date = tradingDay,
                slot_time = SlotTimeChicago,
                bar_count = snapshot.bar_count,
                range_high = snapshot.range_high,
                range_low = snapshot.range_low,
                last_processed_bar_time = snapshot.last_processed_bar_time_utc
            }));

        // Slot-time-passed: attempt immediate lock
        if (utcNow >= SlotTimeUtc)
        {
            LogHealth("INFO", "RANGE_BUILDING_RESTORE_SLOT_PASSED_LOCK_ATTEMPT",
                "Slot time already passed - attempting immediate lock from restored state",
                new
                {
                    stream_id = streamId,
                    trading_date = tradingDay,
                    slot_time = SlotTimeChicago,
                    bar_count = snapshot.bar_count,
                    range_high = snapshot.range_high,
                    range_low = snapshot.range_low
                });

            if (TryLockRange(utcNow))
            {
                return;
            }
        }
    }

    /// <summary>
    /// When RANGE_BUILDING restore fails: transition to PRE_HYDRATION so BarsRequest triggers rebuild.
    /// No silent fallback to ARMED - deterministic rebuild from historical bars.
    /// </summary>
    private void FallbackToRebuild(string tradingDay, string streamId, DateTimeOffset utcNow, string failureReason, RestoreDiagnostics? diag)
    {
        State = StreamState.PRE_HYDRATION;
        _preHydrationComplete = false; // Wait for BarsRequest bars

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "RANGE_BUILDING_RESTORE_FALLBACK_REBUILD", State.ToString(),
            new
            {
                stream = streamId,
                trading_date = tradingDay,
                failure_reason = failureReason,
                snapshot_file_path = diag?.file_path,
                file_exists = diag?.file_exists,
                total_lines_in_file = diag?.total_lines ?? 0,
                matching_lines_for_stream = diag?.stream_line_count ?? 0,
                note = "BarsRequest will be triggered for deterministic rebuild"
            }));
    }

    /// <summary>
    /// Persist RANGE_BUILDING snapshot for restart recovery.
    /// Call when entering RANGE_BUILDING and when range_high/range_low changes.
    /// </summary>
    private void PersistRangeBuildingSnapshot(DateTimeOffset lastBarUtc)
    {
        if (_rangeBuildingSnapshotPersister == null || State != StreamState.RANGE_BUILDING || _rangeLocked)
            return;

        var bars = GetBarBufferSnapshot();
        var snapshotBars = new List<RangeBuildingSnapshotBar>();
        foreach (var b in bars)
        {
            snapshotBars.Add(new RangeBuildingSnapshotBar
            {
                timestamp_utc = b.TimestampUtc.ToString("o"),
                open = b.Open,
                high = b.High,
                low = b.Low,
                close = b.Close
            });
        }

        var snapshot = new RangeBuildingSnapshot
        {
            source = RangeBuildingSnapshot.SourceMarker,
            trading_date = TradingDate ?? "",
            stream_id = Stream ?? "",
            instrument = CanonicalInstrument ?? "",
            session = Session ?? "",
            slot_time = SlotTimeChicago ?? "",
            range_start_chicago = RangeStartChicagoTime.ToString("o"),
            range_start_utc = RangeStartUtc.ToString("o"),
            last_processed_bar_time_utc = lastBarUtc.ToString("o"),
            bar_count = bars.Count,
            range_high = RangeHigh,
            range_low = RangeLow,
            freeze_close = FreezeClose,
            freeze_close_source = FreezeCloseSource ?? "",
            tick_size = _tickSize,
            snapshot_timestamp_utc = DateTimeOffset.UtcNow.ToString("o"),
            bars = snapshotBars
        };

        _rangeBuildingSnapshotPersister.Persist(snapshot);

        var pathInfo = _rangeBuildingSnapshotPersister.GetPathInfo(TradingDate ?? "");
        _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "RANGE_BUILDING_SNAPSHOT_PATH_INFO", State.ToString(),
            new
            {
                resolved_project_root = pathInfo.resolved_project_root,
                absolute_snapshot_path = pathInfo.absolute_snapshot_path,
                note = "write_time"
            }));
        _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "RANGE_BUILDING_SNAPSHOT_WRITTEN", State.ToString(),
            new
            {
                stream_id = Stream,
                trading_date = TradingDate,
                slot_time = SlotTimeChicago,
                bar_count = snapshot.bar_count,
                range_high = snapshot.range_high,
                range_low = snapshot.range_low,
                last_processed_bar_time = snapshot.last_processed_bar_time_utc,
                snapshot_file_path = pathInfo.absolute_snapshot_path
            }));
    }
}
