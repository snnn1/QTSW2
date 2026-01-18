// NinjaTrader BarsRequest helper for requesting historical bars
// This file is compiled only when NINJATRADER is defined (inside NT Strategy context)

#if NINJATRADER
using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using QTSW2.Robot.Core;

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
        /// <param name="startTimeUtc">Start time (UTC)</param>
        /// <param name="endTimeUtc">End time (UTC)</param>
        /// <param name="barSizeMinutes">Bar size in minutes (default: 1)</param>
        /// <returns>List of bars in chronological order</returns>
        public static List<Bar> RequestHistoricalBars(
            Instrument instrument,
            DateTimeOffset startTimeUtc,
            DateTimeOffset endTimeUtc,
            int barSizeMinutes = 1)
        {
            var bars = new List<Bar>();

            try
            {
                // Create BarsRequest for historical data
                var barsRequest = new BarsRequest(instrument)
                {
                    BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Minute, Value = barSizeMinutes },
                    StartTime = startTimeUtc.DateTime,
                    EndTime = endTimeUtc.DateTime,
                    TradingHours = instrument.MasterInstrument.TradingHours
                };

                // Request bars synchronously
                // Note: Request() may return null if no data available or request fails
                var barsSeries = barsRequest.Request();

                if (barsSeries != null && barsSeries.Count > 0)
                {
                    // Convert NinjaTrader bars to Robot.Core.Bar format
                    for (int i = 0; i < barsSeries.Count; i++)
                    {
                        var ntBar = barsSeries.Get(i);
                        
                        // Convert bar time from exchange time to UTC
                        var barExchangeTime = ntBar.Time; // Exchange time (Chicago, Unspecified kind)
                        var barUtc = NinjaTraderExtensions.ConvertBarTimeToUtc(barExchangeTime);
                        
                        // Create Bar struct
                        var bar = new Bar(
                            timestampUtc: barUtc,
                            open: (decimal)ntBar.Open,
                            high: (decimal)ntBar.High,
                            low: (decimal)ntBar.Low,
                            close: (decimal)ntBar.Close,
                            volume: ntBar.Volume > 0 ? (decimal?)ntBar.Volume : null
                        );
                        
                        bars.Add(bar);
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
        /// <returns>List of bars in chronological order</returns>
        public static List<Bar> RequestBarsForTradingDate(
            Instrument instrument,
            DateOnly tradingDate,
            string rangeStartChicago,
            string slotTimeChicago,
            TimeService timeService)
        {
            // Construct Chicago times for the trading date
            var rangeStartChicagoTime = timeService.ConstructChicagoTime(tradingDate, rangeStartChicago);
            var slotTimeChicagoTime = timeService.ConstructChicagoTime(tradingDate, slotTimeChicago);
            
            // Convert to UTC for BarsRequest
            var rangeStartUtc = rangeStartChicagoTime.ToUniversalTime();
            var slotTimeUtc = slotTimeChicagoTime.ToUniversalTime();
            
            // Request bars for this range
            return RequestHistoricalBars(instrument, rangeStartUtc, slotTimeUtc, barSizeMinutes: 1);
        }
    }
}
#endif
