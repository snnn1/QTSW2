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

                // Use ManualResetEvent to wait for async callback
                var waitHandle = new System.Threading.ManualResetEventSlim(false);
                
                // Request bars asynchronously using callback
                barsRequest.Request((request, errorCode, errorMessage) =>
                {
                    try
                    {
                        if (errorCode == ErrorCode.NoError && request.Bars != null)
                        {
                            barsSeries = request.Bars;
                        }
                    }
                    catch (Exception callbackEx)
                    {
                        // Catch any errors in callback (e.g., DateTimeOffset creation issues)
                        throw new InvalidOperationException($"BarsRequest callback error: {callbackEx.Message}", callbackEx);
                    }
                    finally
                    {
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
                                try
                                {
                                    firstBarTime = NinjaTraderExtensions.ConvertBarTimeToUtc(barsSeries.GetTime(0)).ToString("o");
                                }
                                catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException)
                                {
                                    // Index 0 may be invalid - ignore
                                }
                                
                                try
                                {
                                    lastBarTime = NinjaTraderExtensions.ConvertBarTimeToUtc(barsSeries.GetTime(rawBars - 1)).ToString("o");
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
                    for (int i = 0; i < barsCount; i++)
                    {
                        try
                        {
                            // Access bar data using Bars collection methods
                            var barExchangeTime = barsSeries.GetTime(i); // Exchange time (Chicago, Unspecified kind)
                            var barUtc = NinjaTraderExtensions.ConvertBarTimeToUtc(barExchangeTime);
                            
                            // Skip bars outside requested range (compare in UTC)
                            if (barUtc < startUtcForComparison || barUtc >= endUtcForComparison)
                                continue;
                            
                            // Get OHLCV data using Bars collection methods
                            var open = (decimal)barsSeries.GetOpen(i);
                            var high = (decimal)barsSeries.GetHigh(i);
                            var low = (decimal)barsSeries.GetLow(i);
                            var close = (decimal)barsSeries.GetClose(i);
                            var volume = barsSeries.GetVolume(i);
                            
                            // Create CoreBar struct
                            var bar = new CoreBar(
                                timestampUtc: barUtc,
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
        /// </summary>
        /// <param name="instrument">NinjaTrader instrument</param>
        /// <param name="tradingDate">Trading date (Chicago time)</param>
        /// <param name="rangeStartChicago">Range start time (Chicago time, e.g., "07:30")</param>
        /// <param name="slotTimeChicago">Slot time (Chicago time, e.g., "09:00")</param>
        /// <param name="timeService">TimeService for timezone conversions</param>
        /// <param name="logCallback">Optional callback for logging events</param>
        /// <returns>List of bars in chronological order</returns>
        public static List<CoreBar> RequestBarsForTradingDate(
            Instrument instrument,
            QTSW2.Robot.Core.DateOnly tradingDate,
            string rangeStartChicago,
            string slotTimeChicago,
            QTSW2.Robot.Core.TimeService timeService,
            Action<string, object>? logCallback = null)
        {
            // Construct Chicago times for the trading date
            var rangeStartChicagoTime = timeService.ConstructChicagoTime(tradingDate, rangeStartChicago);
            var slotTimeChicagoTime = timeService.ConstructChicagoTime(tradingDate, slotTimeChicago);
            
            // Extract DateTime (already Unspecified kind) - BarsRequest wants Chicago local time, no offsets
            var rangeStartLocal = rangeStartChicagoTime.DateTime; // Already Unspecified kind from ConstructChicagoTime
            var slotTimeLocal = slotTimeChicagoTime.DateTime; // Already Unspecified kind from ConstructChicagoTime
            
            // Request bars directly with Chicago local DateTime - no UTC conversion needed
            return RequestHistoricalBars(instrument, rangeStartLocal, slotTimeLocal, barSizeMinutes: 1, logCallback: logCallback);
        }
    }
}
