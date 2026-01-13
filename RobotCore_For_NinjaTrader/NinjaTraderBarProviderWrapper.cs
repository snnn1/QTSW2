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
/// </summary>
public class NinjaTraderBarProviderWrapper : IBarProvider
{
    private readonly RobotSimStrategy _strategy;
    private readonly RobotEngine _engine;
    private NinjaTraderBarProvider? _provider;

    public NinjaTraderBarProviderWrapper(RobotSimStrategy strategy, RobotEngine engine)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    private NinjaTraderBarProvider GetProvider()
    {
        if (_provider == null)
        {
            // Get TimeService from engine using accessor method (replaces reflection)
            var timeService = _engine.GetTimeService();
            
            if (timeService == null)
                throw new InvalidOperationException("TimeService not available in RobotEngine");

            _provider = new NinjaTraderBarProvider(_strategy, timeService);
        }
        return _provider;
    }

    public IEnumerable<Bar> GetBars(string instrument, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        return GetProvider().GetBars(instrument, startUtc, endUtc);
    }
}
