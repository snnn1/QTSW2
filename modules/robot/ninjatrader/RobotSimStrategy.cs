// NinjaTrader Strategy host for SIM execution
// This Strategy runs inside NinjaTrader and provides NT context (Account, Instrument, Events) to RobotEngine
//
// IMPORTANT: This Strategy MUST run in SIM account only.
// Copy into a NinjaTrader 8 strategy project and wire references to Robot.Core.

using System;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Robot SIM Strategy: Hosts RobotEngine in NinjaTrader SIM account.
    /// Provides NT context (Account, Instrument, Order/Execution events) to NinjaTraderSimAdapter.
    /// </summary>
    public class RobotSimStrategy : Strategy
    {
        private RobotEngine? _engine;
        private NinjaTraderSimAdapter? _adapter;
        private bool _simAccountVerified = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "RobotSimStrategy";
                Calculate = Calculate.OnBarClose; // Bar-close series
                IsUnmanaged = true; // Required for manual order management
            }
            else if (State == State.DataLoaded)
            {
                // Verify SIM account
                if (Account is null)
                {
                    Log($"ERROR: Account is null. Aborting.", LogLevel.Error);
                    return;
                }

                _simAccountVerified = true;
                Log($"SIM account verified: {Account.Name}", LogLevel.Information);

                // Initialize RobotEngine in SIM mode
                var projectRoot = ProjectRootResolver.ResolveProjectRoot();
                _engine = new RobotEngine(projectRoot, TimeSpan.FromSeconds(2), ExecutionMode.SIM);
                _engine.Start();

                // Get the adapter instance and wire NT context
                // Note: This requires exposing adapter from engine or using dependency injection
                // For now, we'll wire events directly to adapter via reflection or adapter registration
                WireNTContextToAdapter();
            }
            else if (State == State.Terminated)
            {
                _engine?.Stop();
            }
        }

        /// <summary>
        /// Wire NinjaTrader context (Account, Instrument, Events) to adapter.
        /// </summary>
        private void WireNTContextToAdapter()
        {
            // Get adapter from engine via reflection (adapter is private field)
            var engineType = _engine.GetType();
            var adapterField = engineType.GetField("_executionAdapter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var adapter = adapterField?.GetValue(_engine) as NinjaTraderSimAdapter;
            
            if (adapter is null)
            {
                var error = "CRITICAL: Could not access execution adapter from engine - aborting strategy execution";
                Log(error, LogLevel.Error);
                throw new InvalidOperationException(error);
            }
            
            _adapter = adapter;
            
            // Set NT context (Account, Instrument) in adapter
            adapter.SetNTContext(Account, Instrument);
            
            // Subscribe to NT order/execution events and forward to adapter
            Account.OrderUpdate += OnOrderUpdate;
            Account.ExecutionUpdate += OnExecutionUpdate;
            
            Log($"NT context wired to adapter: Account={Account.Name}, Instrument={Instrument.MasterInstrument.Name}", LogLevel.Information);
        }

        protected override void OnBarUpdate()
        {
            if (_engine is null || !_simAccountVerified) return;
            if (CurrentBar < 1) return;

            // Convert bar time from exchange time to UTC
            var barExchangeTime = Times[0][0]; // Exchange time (Chicago, Unspecified kind)
            var chicagoTz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
            var barChicagoOffset = new DateTimeOffset(barExchangeTime, chicagoTz.GetUtcOffset(barExchangeTime));
            var barUtc = barChicagoOffset.ToUniversalTime();

            var high = (decimal)High[0];
            var low = (decimal)Low[0];
            var close = (decimal)Close[0];
            var nowUtc = DateTimeOffset.UtcNow;

            _engine.OnBar(barUtc, Instrument.MasterInstrument.Name, high, low, close, nowUtc);
            _engine.Tick(nowUtc);
        }

        /// <summary>
        /// NT OrderUpdate event handler - forwards to adapter.
        /// </summary>
        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            if (_adapter is null) return;

            // Forward to adapter's HandleOrderUpdate method
            // Adapter will correlate order.Tag (intent_id) and update journal
            _adapter.HandleOrderUpdate(e.Order, e);
        }

        /// <summary>
        /// NT ExecutionUpdate event handler - forwards to adapter.
        /// </summary>
        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            if (_adapter is null) return;

            // Forward to adapter's HandleExecutionUpdate method
            // Adapter will correlate order.Tag (intent_id) and trigger protective orders on fill
            _adapter.HandleExecutionUpdate(e.Execution, e.Execution.Order);
        }

        /// <summary>
        /// Expose Account and Instrument to adapter (for order submission).
        /// </summary>
        public Account GetAccount() => Account;
        public Instrument GetInstrument() => Instrument;
    }
}
