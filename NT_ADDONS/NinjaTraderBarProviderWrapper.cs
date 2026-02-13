// NOTE: This file is NT-specific and exists only in RobotCore_For_NinjaTrader/
// It wraps NinjaTraderBarProvider and accesses RobotEngine via accessor methods.

using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core;

/// <summary>
/// Wrapper for NinjaTraderBarProvider that lazily accesses TimeService from RobotEngine.
/// Uses BarsRequest API first, then falls back to Bars collection.
/// </summary>
public class NinjaTraderBarProviderWrapper : IBarProvider
{
    private readonly RobotSimStrategy _strategy;
    private readonly RobotEngine _engine;
    private NinjaTraderBarProviderRequest? _provider;

    public NinjaTraderBarProviderWrapper(RobotSimStrategy strategy, RobotEngine engine)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    private NinjaTraderBarProviderRequest GetProvider()
    {
        if (_provider == null)
        {
            // Get TimeService from engine using accessor method (replaces reflection)
            var timeService = _engine.GetTimeService();
            
            if (timeService == null)
                throw new InvalidOperationException("TimeService not available in RobotEngine");

            // Use BarsRequest-based provider (tries API first, falls back to Bars collection)
            _provider = new NinjaTraderBarProviderRequest(_strategy, timeService);
        }
        return _provider;
    }

    public IEnumerable<Bar> GetBars(string instrument, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        // DIAGNOSTIC: Log snapshot details using strategy's Log method
        // This will appear in NinjaTrader's Output window and can be captured
        try
        {
            var provider = GetProvider();
            var bars = provider.GetBars(instrument, startUtc, endUtc).ToList();
            
            // Log snapshot details using Debug.WriteLine (appears in NinjaTrader Output window)
            var timeService = _engine.GetTimeService();
            if (timeService != null)
            {
                var requestedStartChicago = timeService.ConvertUtcToChicago(startUtc).DateTime;
                var requestedEndChicago = timeService.ConvertUtcToChicago(endUtc).DateTime;
                
                if (bars.Count > 0)
                {
                    var firstBarChicago = timeService.ConvertUtcToChicago(bars[0].TimestampUtc).DateTime;
                    var lastBarChicago = timeService.ConvertUtcToChicago(bars[bars.Count - 1].TimestampUtc).DateTime;
                    
                    System.Diagnostics.Debug.WriteLine($"[BarProvider] Snapshot: {bars.Count} bars returned. " +
                        $"Snapshot range: {firstBarChicago:yyyy-MM-dd HH:mm:ss} to {lastBarChicago:yyyy-MM-dd HH:mm:ss} (Chicago). " +
                        $"Requested: {requestedStartChicago:yyyy-MM-dd HH:mm:ss} to {requestedEndChicago:yyyy-MM-dd HH:mm:ss} (Chicago).");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[BarProvider] Snapshot: 0 bars returned. " +
                        $"Requested: {requestedStartChicago:yyyy-MM-dd HH:mm:ss} to {requestedEndChicago:yyyy-MM-dd HH:mm:ss} (Chicago). " +
                        $"Check: Does NinjaTrader Bars collection have data for this range?");
                }
            }
            
            return bars;
        }
        catch (Exception ex)
        {
            // Log error using Debug.WriteLine (appears in NinjaTrader Output window)
            System.Diagnostics.Debug.WriteLine($"[BarProvider] Error in GetBars: {ex.Message}");
            return Enumerable.Empty<Bar>();
        }
    }
}
