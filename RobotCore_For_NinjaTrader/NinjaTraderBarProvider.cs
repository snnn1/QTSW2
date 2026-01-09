using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core;

/// <summary>
/// NinjaTrader bar provider for accessing historical bars from the Bars collection.
/// Used for late-start hydration in SIM mode.
/// </summary>
public class NinjaTraderBarProvider : IBarProvider
{
    private readonly Strategy _strategy;
    private readonly TimeService _timeService;

    public NinjaTraderBarProvider(Strategy strategy, TimeService timeService)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _timeService = timeService ?? throw new ArgumentNullException(nameof(timeService));
    }

    public IEnumerable<Bar> GetBars(string instrument, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (_strategy.Bars == null || _strategy.Bars.Count == 0)
            yield break;

        // DEFENSIVE: Validate collections are available
        // In NinjaTrader, Times/Opens/Highs/Lows/Closes are indexers that return collections
        // We need to access [0] first to get the collection, then check Count
        try
        {
            if (_strategy.Times[0] == null || _strategy.Times[0].Count == 0)
                yield break;
            if (_strategy.Opens[0] == null || _strategy.Opens[0].Count == 0)
                yield break;
            if (_strategy.Highs[0] == null || _strategy.Highs[0].Count == 0)
                yield break;
            if (_strategy.Lows[0] == null || _strategy.Lows[0].Count == 0)
                yield break;
            if (_strategy.Closes[0] == null || _strategy.Closes[0].Count == 0)
                yield break;
        }
        catch
        {
            // If any collection is unavailable, skip hydration (should never crash)
            yield break;
        }

        var barCount = _strategy.Bars.Count;
        var timesCount = _strategy.Times[0].Count;

        // Convert UTC times to Chicago time for comparison
        var startChicago = _timeService.ConvertUtcToChicago(startUtc);
        var endChicago = _timeService.ConvertUtcToChicago(endUtc);

        // Iterate through NinjaTrader bars
        // Bars collection is indexed from 0 (oldest) to Count-1 (newest)
        for (int i = 0; i < barCount && i < timesCount; i++)
        {
            // DEFENSIVE: Validate index is within bounds
            if (i >= _strategy.Times[0].Count || 
                i >= _strategy.Opens[0].Count || 
                i >= _strategy.Highs[0].Count || 
                i >= _strategy.Lows[0].Count || 
                i >= _strategy.Closes[0].Count)
            {
                // Index out of bounds - skip this bar (hydration should never crash)
                // Note: We can't log here as we don't have access to logger in this context
                // The calling code will handle missing bars gracefully
                continue;
            }

            // Get bar time (Times[0][i] is the bar timestamp in exchange time)
            var barExchangeTime = _strategy.Times[0][i];
            
            // Convert exchange time to DateTimeOffset with Chicago timezone
            var chicagoTz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
            var barChicagoOffset = new DateTimeOffset(barExchangeTime, chicagoTz.GetUtcOffset(barExchangeTime));
            var barUtc = barChicagoOffset.ToUniversalTime();

            // Check if bar is in the requested range (using Chicago time for comparison)
            var barChicagoTime = _timeService.ConvertUtcToChicago(barUtc);
            
            if (barChicagoTime >= startChicago && barChicagoTime < endChicago)
            {
                // Bar is in range - yield it
                yield return new Bar(
                    barUtc,
                    (decimal)_strategy.Opens[0][i],
                    (decimal)_strategy.Highs[0][i],
                    (decimal)_strategy.Lows[0][i],
                    (decimal)_strategy.Closes[0][i],
                    _strategy.Volumes != null && _strategy.Volumes[0] != null && _strategy.Volumes[0].Count > 0 && i < _strategy.Volumes[0].Count ? (decimal?)_strategy.Volumes[0][i] : null
                );
            }
            
            // Early exit if we've passed the end time
            if (barChicagoTime >= endChicago)
                break;
        }
    }
}
