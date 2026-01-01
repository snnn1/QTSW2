// NinjaTrader wrapper (thin adapter)
// IMPORTANT: This is a skeleton and MUST NEVER place orders.
//
// This file intentionally avoids being part of any build in this repo.
// Copy into a NinjaTrader 8 strategy project and wire references to the core engine.

#if NINJATRADER
using System;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using QTSW2.Robot.Core;

namespace NinjaTrader.Custom.Strategies
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
                _engine = new RobotEngine(projectRoot, TimeSpan.FromSeconds(2));
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

            // NinjaTrader Times[0][0] is in exchange time; convert to UTC if needed.
            // For skeleton: treat bar time as UTC if you feed UTC. Otherwise convert explicitly.
            var nowUtc = DateTimeOffset.UtcNow;
            var barUtc = nowUtc; // TODO: map NT bar time to UTC deterministically

            var high = (decimal)High[0];
            var low = (decimal)Low[0];
            var close = (decimal)Close[0];

            _engine.OnBar(barUtc, Instrument.MasterInstrument.Name, high, low, close, nowUtc);
            _engine.Tick(nowUtc);

            // HARD CONSTRAINT: Never call EnterLong/EnterShort/SubmitOrderUnmanaged/etc.
        }
    }
}
#endif

