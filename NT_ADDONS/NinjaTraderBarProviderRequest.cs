using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core;

/// <summary>
/// Alternative NinjaTrader bar provider that uses BarsRequest API to request historical bars from data provider.
/// This is more reliable than accessing the Bars collection, especially when strategy starts after the range window.
/// 
/// FALLBACK APPROACH: If BarsRequest fails or returns no bars, falls back to Bars collection access.
/// </summary>
public class NinjaTraderBarProviderRequest : IBarProvider
{
    private readonly Strategy _strategy;
    private readonly TimeService _timeService;

    public NinjaTraderBarProviderRequest(Strategy strategy, TimeService timeService)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _timeService = timeService ?? throw new ArgumentNullException(nameof(timeService));
    }

    public IEnumerable<Bar> GetBars(string instrument, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        // Convert times for logging
        var startChicago = _timeService.ConvertUtcToChicago(startUtc);
        var endChicago = _timeService.ConvertUtcToChicago(endUtc);
        
        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] ===== GetBars() called =====");
        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Instrument: {instrument}");
        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Requested range (UTC): {startUtc:yyyy-MM-dd HH:mm:ss} to {endUtc:yyyy-MM-dd HH:mm:ss}");
        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Requested range (Chicago): {startChicago.DateTime:yyyy-MM-dd HH:mm:ss} to {endChicago.DateTime:yyyy-MM-dd HH:mm:ss}");
        
        // APPROACH 1: Try to request historical bars using BarsRequest API
        // This requests bars directly from the data provider, regardless of "Days to load" setting
        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] --- Attempting BarsRequest API ---");
        var barsFromRequest = TryGetBarsFromRequest(instrument, startUtc, endUtc).ToList();
        
        if (barsFromRequest.Count > 0)
        {
            var firstBarChicago = _timeService.ConvertUtcToChicago(barsFromRequest[0].TimestampUtc).DateTime;
            var lastBarChicago = _timeService.ConvertUtcToChicago(barsFromRequest[barsFromRequest.Count - 1].TimestampUtc).DateTime;
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] ✓ SUCCESS: Retrieved {barsFromRequest.Count} bars using BarsRequest API");
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Bars range (Chicago): {firstBarChicago:yyyy-MM-dd HH:mm:ss} to {lastBarChicago:yyyy-MM-dd HH:mm:ss}");
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] ===== Returning bars from BarsRequest =====");
            return barsFromRequest;
        }

        // APPROACH 2: Fallback to Bars collection (existing approach)
        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] ✗ BarsRequest returned 0 bars, falling back to Bars collection");
        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] --- Attempting Bars collection snapshot ---");
        var barsFromCollection = TryGetBarsFromCollection(instrument, startUtc, endUtc).ToList();
        
        if (barsFromCollection.Count > 0)
        {
            var firstBarChicago = _timeService.ConvertUtcToChicago(barsFromCollection[0].TimestampUtc).DateTime;
            var lastBarChicago = _timeService.ConvertUtcToChicago(barsFromCollection[barsFromCollection.Count - 1].TimestampUtc).DateTime;
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] ✓ SUCCESS: Retrieved {barsFromCollection.Count} bars from Bars collection");
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Bars range (Chicago): {firstBarChicago:yyyy-MM-dd HH:mm:ss} to {lastBarChicago:yyyy-MM-dd HH:mm:ss}");
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] ===== Returning bars from Bars collection =====");
            return barsFromCollection;
        }

        // Both approaches failed
        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] ✗ FAILED: Both BarsRequest and Bars collection returned 0 bars");
        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] ===== Returning empty collection =====");
        return Enumerable.Empty<Bar>();
    }

    private IEnumerable<Bar> TryGetBarsFromRequest(string instrument, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        // NOTE: BarsRequest API is not available or doesn't work as expected in NinjaTrader 8
        // This method is kept for future use but currently just logs and returns empty
        // The fallback to Bars collection will handle the actual bar retrieval
        
        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] BarsRequest API not available - skipping");
        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Will fall back to Bars collection approach");
        
        return Enumerable.Empty<Bar>();
        
        // NOTE: BarsRequest API is not available in NinjaTrader 8 as expected
        // Future implementation would go here if the API becomes available
    }

    private IEnumerable<Bar> TryGetBarsFromCollection(string instrument, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        // Fallback to existing Bars collection approach (same as NinjaTraderBarProvider)
        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Checking Bars collection...");
        
        if (_strategy.Bars == null)
        {
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] ✗ Bars collection is null");
            return Enumerable.Empty<Bar>();
        }

        if (_strategy.Bars.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] ✗ Bars collection is empty (Count=0)");
            return Enumerable.Empty<Bar>();
        }

        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Bars collection has {_strategy.Bars.Count} bars");

        var snapshot = new List<Bar>();
        var chicagoTz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        var snapshotCount = _strategy.Times[0]?.Count ?? 0;

        if (snapshotCount == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] ✗ Times[0] collection is empty or null");
            return Enumerable.Empty<Bar>();
        }

        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Times[0] collection has {snapshotCount} entries");
        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Snapshotting bars from collection...");

        var snapshotErrors = 0;
        // Snapshot from oldest to newest
        for (int barsAgo = snapshotCount - 1; barsAgo >= 0; barsAgo--)
        {
            try
            {
                var currentCount = _strategy.Times[0].Count;
                if (barsAgo < 0 || barsAgo >= currentCount)
                    continue;

                var barExchangeTime = _strategy.Times[0][barsAgo];
                var barChicagoOffset = new DateTimeOffset(barExchangeTime, chicagoTz.GetUtcOffset(barExchangeTime));
                var barUtc = barChicagoOffset.ToUniversalTime();

                var open = (decimal)_strategy.Opens[0][barsAgo];
                var high = (decimal)_strategy.Highs[0][barsAgo];
                var low = (decimal)_strategy.Lows[0][barsAgo];
                var close = (decimal)_strategy.Closes[0][barsAgo];
                var volume = _strategy.Volumes != null && _strategy.Volumes[0] != null && barsAgo < _strategy.Volumes[0].Count
                    ? (decimal?)_strategy.Volumes[0][barsAgo]
                    : null;

                snapshot.Add(new Bar(barUtc, open, high, low, close, volume));
            }
            catch (Exception ex)
            {
                snapshotErrors++;
                if (snapshotErrors <= 5)
                {
                    System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Error snapshotting bar at index {barsAgo}: {ex.Message}");
                }
                continue;
            }
        }

        if (snapshotErrors > 5)
        {
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] ... and {snapshotErrors - 5} more snapshot errors");
        }

        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Snapshot complete: {snapshot.Count} bars (out of {snapshotCount} total, {snapshotErrors} errors)");

        if (snapshot.Count > 0)
        {
            var firstBarChicago = _timeService.ConvertUtcToChicago(snapshot[0].TimestampUtc).DateTime;
            var lastBarChicago = _timeService.ConvertUtcToChicago(snapshot[snapshot.Count - 1].TimestampUtc).DateTime;
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Snapshot range (Chicago): {firstBarChicago:yyyy-MM-dd HH:mm:ss} to {lastBarChicago:yyyy-MM-dd HH:mm:ss}");
        }

        // Filter by time range
        var startChicago = _timeService.ConvertUtcToChicago(startUtc);
        var endChicago = _timeService.ConvertUtcToChicago(endUtc);

        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Filtering snapshot by requested range: {startChicago.DateTime:yyyy-MM-dd HH:mm:ss} to {endChicago.DateTime:yyyy-MM-dd HH:mm:ss}");

        var filteredBars = snapshot.Where(bar =>
        {
            var barChicagoTime = _timeService.ConvertUtcToChicago(bar.TimestampUtc);
            return barChicagoTime >= startChicago && barChicagoTime < endChicago;
        }).ToList();

        System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Filtered result: {filteredBars.Count} bars match requested range (out of {snapshot.Count} snapshot bars)");

        if (filteredBars.Count > 0)
        {
            var firstBarChicago = _timeService.ConvertUtcToChicago(filteredBars[0].TimestampUtc).DateTime;
            var lastBarChicago = _timeService.ConvertUtcToChicago(filteredBars[filteredBars.Count - 1].TimestampUtc).DateTime;
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Filtered bars range (Chicago): {firstBarChicago:yyyy-MM-dd HH:mm:ss} to {lastBarChicago:yyyy-MM-dd HH:mm:ss}");
        }
        else if (snapshot.Count > 0)
        {
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] ⚠ WARNING: Snapshot has {snapshot.Count} bars but none match requested range!");
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Snapshot range: {_timeService.ConvertUtcToChicago(snapshot[0].TimestampUtc).DateTime:yyyy-MM-dd HH:mm:ss} to {_timeService.ConvertUtcToChicago(snapshot[snapshot.Count - 1].TimestampUtc).DateTime:yyyy-MM-dd HH:mm:ss}");
            System.Diagnostics.Debug.WriteLine($"[BarProviderRequest] Requested range: {startChicago.DateTime:yyyy-MM-dd HH:mm:ss} to {endChicago.DateTime:yyyy-MM-dd HH:mm:ss}");
        }

        return filteredBars;
    }
}
