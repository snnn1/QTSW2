// NinjaTrader wrapper (thin adapter)
// IMPORTANT: This is a skeleton and MUST NEVER place orders.
//
// This file intentionally avoids being part of any build in this repo.
// Copy into a NinjaTrader 8 strategy project and wire references to the core engine.

using System;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using QTSW2.Robot.Core;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class RobotSkeletonStrategy : Strategy
    {
        private RobotEngine? _engine;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "RobotSkeletonStrategy";
                Calculate = Calculate.OnBarClose; // bar-close series; matches skeleton freeze_close_source=BAR_CLOSE
                IsUnmanaged = true; // safety: unmanaged, but we still will not submit orders
            }
            else if (State == State.DataLoaded)
            {
                var projectRoot = QTSW2.Robot.Core.ProjectRootResolver.ResolveProjectRoot();
                // Default to DRYRUN mode for Step-3; can be made configurable via strategy parameters
                _engine = new RobotEngine(projectRoot, TimeSpan.FromSeconds(2), RobotMode.DRYRUN);
                _engine.Start();
            }
            else if (State == State.Terminated)
            {
                _engine?.Stop();
            }
        }

        protected override void OnBarUpdate()
        {
            if (_engine is null) return;
            if (CurrentBar < 1) return;

            // NinjaTrader Times[0][0] is in exchange time (typically Chicago time for futures).
            // Convert to UTC deterministically using TimeService conversion logic.
            // Times[0][0] is a DateTime representing the bar's timestamp in exchange timezone.
            var nowUtc = DateTimeOffset.UtcNow;
            
            // Convert bar time from exchange time to UTC
            // Times[0][0] is DateTimeKind.Unspecified, representing exchange local time (Chicago)
            // We need to interpret it as Chicago time and convert to UTC (DST-aware)
            var barExchangeTime = Times[0][0]; // Exchange time (Chicago, Unspecified kind)
            
            // Create DateTimeOffset with Chicago timezone, then convert to UTC
            // Note: This assumes Times[0][0] is already in Chicago time (which is standard for futures)
            // If your data feed uses a different timezone, adjust accordingly
            var chicagoTz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
            var barChicagoOffset = new DateTimeOffset(barExchangeTime, chicagoTz.GetUtcOffset(barExchangeTime));
            var barUtc = barChicagoOffset.ToUniversalTime();

            var high = (decimal)High[0];
            var low = (decimal)Low[0];
            var close = (decimal)Close[0];

            _engine.OnBar(barUtc, Instrument.MasterInstrument.Name, high, low, close, nowUtc);
            _engine.Tick(nowUtc);

            // HARD CONSTRAINT: Never call EnterLong/EnterShort/SubmitOrderUnmanaged/etc.
        }
    }
}

