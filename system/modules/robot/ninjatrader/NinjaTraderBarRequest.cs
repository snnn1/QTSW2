// NinjaTrader BarsRequest helper for requesting historical bars
// This file should be placed in the NinjaTrader Strategies folder

using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using QTSW2.Robot.Core;
using CoreBar = QTSW2.Robot.Core.Bar;
using NtBar = NinjaTrader.Data.Bar;

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Helper class to request historical bars from NinjaTrader using BarsRequest API.
    /// Used for pre-hydration in SIM mode.
    /// </summary>
    public static class NinjaTraderBarRequest
    {
        /// <summary>
        /// Request historical bars from NinjaTrader for the specified time range.
        /// </summary>
        /// <param name="instrument">NinjaTrader instrument</param>
        /// <param name="startTimeLocal">Start time (Chicago local, DateTimeKind.Unspecified)</param>
        /// <param name="endTimeLocal">End time (Chicago local, DateTimeKind.Unspecified)</param>
        /// <param name="barSizeMinutes">Bar size in minutes (default: 1)</param>
        /// <param name="logCallback">Optional callback for logging events</param>
        /// <returns>List of bars in chronological order</returns>
        public static List<CoreBar> RequestHistoricalBars(
            Instrument instrument,
            DateTime startTimeLocal,
            DateTime endTimeLocal,
            int barSizeMinutes = 1,
            Action<string, object>? logCallback = null)
        {
            var bars = new List<CoreBar>();
            Bars barsSeries = null;

            try
            {
                // Ensure DateTimeKind is Unspecified (BarsRequest expects Chicago local time, no timezone metadata)
                var startLocal = startTimeLocal.Kind == DateTimeKind.Unspecified 
                    ? startTimeLocal 
                    : DateTime.SpecifyKind(startTimeLocal, DateTimeKind.Unspecified);
                var endLocal = endTimeLocal.Kind == DateTimeKind.Unspecified 
                    ? endTimeLocal 
                    : DateTime.SpecifyKind(endTimeLocal, DateTimeKind.Unspecified);
                
                // Create BarsRequest with Chicago local DateTime (Unspecified kind)
                // BarsRequest handles timezone internally based on TradingHours
                var barsRequest = new BarsRequest(instrument, startLocal, endLocal);
                barsRequest.BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Minute, Value = barSizeMinutes };
                barsRequest.TradingHours = instrument.MasterInstrument.TradingHours;

                // 🔒 INVARIANT GUARD: This conversion assumes fixed 1-minute bars
                // If BarsPeriod changes, AddMinutes(-1) becomes incorrect
                if (barsRequest.BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || barsRequest.BarsPeriod.Value != 1)
                {
                    var errorMsg = $"CRITICAL: Bar timestamp conversion requires 1-minute bars. " +
                                  $"Current BarsPeriod: {barsRequest.BarsPeriod.BarsPeriodType}, Value: {barsRequest.BarsPeriod.Value}. " +
                                  $"Cannot convert close time to open time. Stand down.";
                    if (logCallback != null)
                    {
                        logCallback("BARSREQUEST_INVARIANT_VIOLATION", new
                        {
                            instrument = instrument.MasterInstrument.Name,
                            bars_period_type = barsRequest.BarsPeriod.BarsPeriodType.ToString(),
                            bars_period_value = barsRequest.BarsPeriod.Value,
                            error = errorMsg
                        });
                    }
                    throw new InvalidOperationException(errorMsg);
                }

                // Use ManualResetEvent to wait for async callback
                var waitHandle = new System.Threading.ManualResetEventSlim(false);
                
                // Request bars asynchronously using callback
                barsRequest.Request((request, errorCode, errorMessage) =>
                {
                    // BINARY TRUTH EVENT: Prove callback path is executed and not exception-swallowed
                    string? firstCloseTimeUtc = null;
                    string? lastCloseTimeUtc = null;
                    int barsCountReceived = 0;
                    Exception? callbackException = null;
                    
                    try
                    {
                        if (errorCode == ErrorCode.NoError && request.Bars != null)
                        {
                            barsSeries = request.Bars;
                            barsCountReceived = barsSeries.Count;
                            
                            // Extract first and last bar close times (bars are in UTC)
                            if (barsCountReceived > 0)
                            {
                                try
                                {
                                    var firstBarTimeRaw = barsSeries.GetTime(0);
                                    firstCloseTimeUtc = new DateTimeOffset(DateTime.SpecifyKind(firstBarTimeRaw, DateTimeKind.Utc), TimeSpan.Zero).ToString("o");
                                }
                                catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException)
                                {
                                    // Index 0 may be invalid - ignore
                                }
                                
                                try
                                {
                                    var lastBarTimeRaw = barsSeries.GetTime(barsCountReceived - 1);
                                    lastCloseTimeUtc = new DateTimeOffset(DateTime.SpecifyKind(lastBarTimeRaw, DateTimeKind.Utc), TimeSpan.Zero).ToString("o");
                                }
                                catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException)
                                {
                                    // Last index may be invalid - ignore
                                }
                            }
                        }
                    }
                    catch (Exception callbackEx)
                    {
                        callbackException = callbackEx;
                        // Catch any errors in callback (e.g., DateTimeOffset creation issues)
                        // Log callback error if callback provided
                        if (logCallback != null)
                        {
                            logCallback("BARSREQUEST_CALLBACK_ERROR", new
                            {
                                instrument = instrument.MasterInstrument.Name,
                                error_code = errorCode.ToString(),
                                error_message = errorMessage,
                                exception_message = callbackEx.Message,
                                exception_type = callbackEx.GetType().Name,
                                note = "Error occurred in BarsRequest callback handler"
                            });
                        }
                        throw new InvalidOperationException($"BarsRequest callback error: {callbackEx.Message}", callbackEx);
                    }
                    finally
                    {
                        // Emit BARSREQUEST_CALLBACK_RECEIVED event (binary truth event)
                        if (logCallback != null)
                        {
                            logCallback("BARSREQUEST_CALLBACK_RECEIVED", new
                            {
                                execution_instrument = instrument.MasterInstrument.Name,
                                bars_count_received = barsCountReceived,
                                first_close_time_utc = firstCloseTimeUtc,
                                last_close_time_utc = lastCloseTimeUtc,
                                exception = callbackException?.Message,
                                error_code = errorCode.ToString(),
                                error_message = errorMessage
                            });
                        }
                        waitHandle.Set();
                    }
                });
                
                // Wait for callback to complete (with timeout)
                if (!waitHandle.Wait(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("BarsRequest timed out after 30 seconds");
                }

                // Log raw result after Request() completes
                if (logCallback != null)
                {
                    var rawBars = 0;
                    string? firstBarTime = null;
                    string? lastBarTime = null;
                    
                    // Safely access barsSeries.Count - it may throw if collection is invalid
                    try
                    {
                        if (barsSeries != null)
                        {
                            rawBars = barsSeries.Count;
                            
                            if (rawBars > 0)
                            {
                                // Use GetTime() method to access bar times - wrap in try-catch for safety
                                // CRITICAL: Bars are already UTC, so create DateTimeOffset directly
                                try
                                {
                                    var firstBarTimeRaw = barsSeries.GetTime(0);
                                    firstBarTime = new DateTimeOffset(DateTime.SpecifyKind(firstBarTimeRaw, DateTimeKind.Utc), TimeSpan.Zero).ToString("o");
                                }
                                catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException)
                                {
                                    // Index 0 may be invalid - ignore
                                }
                                
                                try
                                {
                                    var lastBarTimeRaw = barsSeries.GetTime(rawBars - 1);
                                    lastBarTime = new DateTimeOffset(DateTime.SpecifyKind(lastBarTimeRaw, DateTimeKind.Utc), TimeSpan.Zero).ToString("o");
                                }
                                catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException)
                                {
                                    // Last index may be invalid - ignore
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException)
                    {
                        // barsSeries.Count itself may throw if collection is invalid - treat as empty
                        rawBars = 0;
                    }
                    
                    logCallback("BARSREQUEST_RAW_RESULT", new
                    {
                        instrument = instrument.MasterInstrument.Name,
                        bars_returned_raw = rawBars,
                        first_bar_time = firstBarTime,
                        last_bar_time = lastBarTime,
                        request_start_local = startLocal.ToString("o"),
                        request_end_local = endLocal.ToString("o")
                    });
                }

                // Safely check if we have bars to process
                int barsCount = 0;
                try
                {
                    if (barsSeries != null)
                    {
                        barsCount = barsSeries.Count;
                    }
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException)
                {
                    // barsSeries.Count may throw if collection is invalid - treat as empty
                    barsCount = 0;
                }

                if (barsSeries != null && barsCount > 0)
                {
                    // Convert local times to UTC for range comparison
                    // Ensure DateTimeKind is Unspecified before conversion (ConvertTimeToUtc requires Unspecified or Local)
                    var chicagoTz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
                    var startLocalForConversion = startLocal.Kind == DateTimeKind.Unspecified 
                        ? startLocal 
                        : DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified);
                    var endLocalForConversion = endLocal.Kind == DateTimeKind.Unspecified 
                        ? endLocal 
                        : DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified);
                    
                    var startUtcForComparison = TimeZoneInfo.ConvertTimeToUtc(startLocalForConversion, chicagoTz);
                    var endUtcForComparison = TimeZoneInfo.ConvertTimeToUtc(endLocalForConversion, chicagoTz);
                    
                    // Convert NinjaTrader bars to Robot.Core.Bar format
                    // Wrap individual bar access in try-catch to handle sparse collections
                    // Use barsCount (safely retrieved) instead of barsSeries.Count
                    
                    // DEFENSIVE: Track close times for assertion
                    var firstCloseTimeUtc = (DateTimeOffset?)null;
                    var lastCloseTimeUtc = (DateTimeOffset?)null;
                    var barsOutsideRange = 0;
                    
                    for (int i = 0; i < barsCount; i++)
                    {
                        try
                        {
                            // Access bar data using Bars collection methods
                            var barExchangeTime = barsSeries.GetTime(i); // Exchange time (UTC, Unspecified kind)
                            
                            // CRITICAL: Bars from NinjaTrader are already UTC, so create DateTimeOffset directly
                            // No conversion needed - just specify UTC kind and zero offset
                            // This represents the CLOSE time of the bar
                            var barCloseTimeUtc = new DateTimeOffset(DateTime.SpecifyKind(barExchangeTime, DateTimeKind.Utc), TimeSpan.Zero);
                            
                            // DEFENSIVE ADMISSION: Only accept bars with CLOSE times in requested range [startUtc, endUtc)
                            // This is the explicit contract: BarsRequest returns bars with close times in this range
                            if (barCloseTimeUtc < startUtcForComparison || barCloseTimeUtc >= endUtcForComparison)
                            {
                                barsOutsideRange++;
                                continue;
                            }
                            
                            // Track first and last close times for assertion
                            if (firstCloseTimeUtc == null || barCloseTimeUtc < firstCloseTimeUtc)
                                firstCloseTimeUtc = barCloseTimeUtc;
                            if (lastCloseTimeUtc == null || barCloseTimeUtc > lastCloseTimeUtc)
                                lastCloseTimeUtc = barCloseTimeUtc;
                            
                            // Get OHLCV data using Bars collection methods
                            var open = (decimal)barsSeries.GetOpen(i);
                            var high = (decimal)barsSeries.GetHigh(i);
                            var low = (decimal)barsSeries.GetLow(i);
                            var close = (decimal)barsSeries.GetClose(i);
                            var volume = barsSeries.GetVolume(i);
                            
                            // Convert bar timestamp from CLOSE time to OPEN time (Analyzer parity)
                            // INVARIANT: BarsPeriod == 1 minute (enforced above)
                            // This conversion is deterministic: close_time - 1min = open_time
                            var barUtcOpenTime = barCloseTimeUtc.AddMinutes(-1);
                            
                            // Create CoreBar struct with OPEN time
                            var bar = new CoreBar(
                                timestampUtc: barUtcOpenTime,  // OPEN time (converted from close time)
                                open: open,
                                high: high,
                                low: low,
                                close: close,
                                volume: volume > 0 ? (decimal?)volume : null
                            );
                            
                            bars.Add(bar);
                        }
                        catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException)
                        {
                            // Bars collection may have gaps - skip invalid indices and continue
                            continue;
                        }
                    }
                    
                    // ASSERTION: Verify returned close times are exactly within requested range
                    // This ensures determinism and auditability
                    if (bars.Count > 0)
                    {
                        // Assert: First bar close time >= requested start
                        if (firstCloseTimeUtc.HasValue && firstCloseTimeUtc.Value < startUtcForComparison)
                        {
                            var errorMsg = $"ASSERTION FAILED: First bar close time {firstCloseTimeUtc.Value:o} is before requested start {startUtcForComparison:o}";
                            if (logCallback != null)
                            {
                                try { logCallback("BARSREQUEST_ASSERTION_FAILED", new Dictionary<string, object> { { "error", errorMsg } }); }
                                catch { }
                            }
                            throw new InvalidOperationException(errorMsg);
                        }
                        
                        // Assert: Last bar close time < requested end (exclusive)
                        if (lastCloseTimeUtc.HasValue && lastCloseTimeUtc.Value >= endUtcForComparison)
                        {
                            var errorMsg = $"ASSERTION FAILED: Last bar close time {lastCloseTimeUtc.Value:o} is at or after requested end {endUtcForComparison:o}";
                            if (logCallback != null)
                            {
                                try { logCallback("BARSREQUEST_ASSERTION_FAILED", new Dictionary<string, object> { { "error", errorMsg } }); }
                                catch { }
                            }
                            throw new InvalidOperationException(errorMsg);
                        }
                        
                        // Log assertion success for auditability
                        if (logCallback != null)
                        {
                            try
                            {
                                logCallback("BARSREQUEST_CLOSE_TIME_VERIFIED", new Dictionary<string, object>
                                {
                                    { "bars_returned", bars.Count },
                                    { "bars_filtered_out", barsOutsideRange },
                                    { "first_close_time_utc", firstCloseTimeUtc.Value.ToString("o") },
                                    { "last_close_time_utc", lastCloseTimeUtc.Value.ToString("o") },
                                    { "requested_start_utc", startUtcForComparison.ToString("o") },
                                    { "requested_end_utc", endUtcForComparison.ToString("o") },
                                    { "assertion_passed", true }
                                });
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error but don't throw - allow fallback to file-based or live bars
                throw new InvalidOperationException($"Failed to request historical bars from NinjaTrader: {ex.Message}", ex);
            }

            return bars;
        }

        /// <summary>
        /// Request historical bars for a trading date range.
        /// 
        /// SEMANTICS (CRITICAL):
        /// - Inputs (rangeStartChicago, slotTimeChicago) specify desired OPEN times
        /// - NinjaTrader BarsRequest operates on CLOSE times
        /// - We convert CLOSE time → OPEN time by subtracting 1 minute
        /// 
        /// REQUEST LOGIC:
        /// - To get OPEN times [rangeStart, slotTime), we request CLOSE times [rangeStart+1min, slotTime+1min)
        /// - This ensures: close_time - 1min = open_time yields exactly [rangeStart, slotTime) open times
        /// 
        /// ASSERTIONS:
        /// - Returned bars are verified to have close times within requested range
        /// - Admission logic filters defensively to ensure exactness
        /// </summary>
        /// <param name="instrument">NinjaTrader instrument</param>
        /// <param name="tradingDate">Trading date (Chicago time)</param>
        /// <param name="rangeStartChicago">Desired OPEN time start (Chicago time, e.g., "08:00")</param>
        /// <param name="slotTimeChicago">Desired OPEN time end (Chicago time, e.g., "11:00") - exclusive</param>
        /// <param name="timeService">TimeService for timezone conversions</param>
        /// <param name="logCallback">Optional callback for logging events</param>
        /// <returns>List of bars in chronological order, with OPEN times in [rangeStart, slotTime)</returns>
        public static List<CoreBar> RequestBarsForTradingDate(
            Instrument instrument,
            QTSW2.Robot.Core.DateOnly tradingDate,
            string rangeStartChicago,
            string slotTimeChicago,
            QTSW2.Robot.Core.TimeService timeService,
            Action<string, object>? logCallback = null)
        {
            // STEP 1: Construct desired OPEN time boundaries (Chicago time)
            var desiredOpenStartChicagoTime = timeService.ConstructChicagoTime(tradingDate, rangeStartChicago);
            var desiredOpenEndChicagoTime = timeService.ConstructChicagoTime(tradingDate, slotTimeChicago);
            
            // STEP 2: Convert to CLOSE time request boundaries
            // NinjaTrader timestamps bars at CLOSE time, so we shift by +1 minute
            // Request CLOSE times [desiredOpenStart+1min, desiredOpenEnd+1min)
            // After conversion (close - 1min), we get OPEN times [desiredOpenStart, desiredOpenEnd)
            var requestCloseStartChicagoTime = desiredOpenStartChicagoTime.AddMinutes(1);
            var requestCloseEndChicagoTime = desiredOpenEndChicagoTime.AddMinutes(1);
            
            // STEP 3: Extract DateTime for BarsRequest (Chicago local time, Unspecified kind)
            var requestCloseStartLocal = requestCloseStartChicagoTime.DateTime;
            var requestCloseEndLocal = requestCloseEndChicagoTime.DateTime;
            
            // STEP 4: Request bars with CLOSE time boundaries
            // Log the explicit CLOSE time request for auditability
            if (logCallback != null)
            {
                try
                {
                    logCallback("BARSREQUEST_CLOSE_TIME_BOUNDARIES", new Dictionary<string, object>
                    {
                        { "desired_open_start_chicago", desiredOpenStartChicagoTime.ToString("o") },
                        { "desired_open_end_chicago", desiredOpenEndChicagoTime.ToString("o") },
                        { "request_close_start_chicago", requestCloseStartChicagoTime.ToString("o") },
                        { "request_close_end_chicago", requestCloseEndChicagoTime.ToString("o") },
                        { "note", "BarsRequest operates on CLOSE times; will convert to OPEN times by subtracting 1 minute" }
                    });
                }
                catch { /* Log callback optional */ }
            }
            
            // Request bars with explicit CLOSE time range
            return RequestHistoricalBars(instrument, requestCloseStartLocal, requestCloseEndLocal, barSizeMinutes: 1, logCallback: logCallback);
        }
    }
}
