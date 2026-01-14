// NinjaTrader wrapper (thin adapter)
// IMPORTANT: This is a skeleton and MUST NEVER place orders.
//
// This file intentionally avoids being part of any build in this repo.
// Copy into a NinjaTrader 8 strategy project and wire references to the core engine.

using System;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using QTSW2.Robot.Core;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class RobotSkeletonStrategy : Strategy
    {
        private RobotEngine? _engine;
        private Timer? _tickTimer;
        private readonly object _timerLock = new object();

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
                _engine = new RobotEngine(projectRoot, TimeSpan.FromSeconds(2), ExecutionMode.DRYRUN);
                
                // PHASE 1: Set account info for startup banner
                var accountName = Account?.Name ?? "UNKNOWN";
                var environment = "DRYRUN";
                _engine.SetAccountInfo(accountName, environment);
                
                _engine.Start();
            }
            else if (State == State.Realtime)
            {
                // PHASE 3: Start periodic timer for time-based state transitions (decoupled from bar arrivals)
                StartTickTimer();
            }
            else if (State == State.Terminated)
            {
                StopTickTimer();
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
            
            // Convert bar time from exchange time to UTC using helper method
            // Times[0][0] is DateTimeKind.Unspecified, representing exchange local time (Chicago)
            var barExchangeTime = Times[0][0]; // Exchange time (Chicago, Unspecified kind)
            var barUtc = NinjaTraderExtensions.ConvertBarTimeToUtc(barExchangeTime);

            var open = (decimal)Open[0];
            var high = (decimal)High[0];
            var low = (decimal)Low[0];
            var close = (decimal)Close[0];

            _engine.OnBar(barUtc, Instrument.MasterInstrument.Name, open, high, low, close, nowUtc);
            // NOTE: Tick() is now called by timer, not by bar arrivals
            // This ensures time-based state transitions occur even when no bars arrive

            // HARD CONSTRAINT: Never call EnterLong/EnterShort/SubmitOrderUnmanaged/etc.
        }
        
        /// <summary>
        /// PHASE 3: Start periodic timer that calls Engine.Tick() every 1 second.
        /// Timer ensures time-based state transitions occur even when no bars arrive.
        /// </summary>
        private void StartTickTimer()
        {
            lock (_timerLock)
            {
                if (_tickTimer != null)
                {
                    // Timer already started, ignore
                    return;
                }

                // Create timer that fires every 1 second
                // Timer callback must be thread-safe and never throw
                _tickTimer = new Timer(TimerCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            }
        }

        /// <summary>
        /// PHASE 3: Stop and dispose the tick timer.
        /// </summary>
        private void StopTickTimer()
        {
            lock (_timerLock)
            {
                if (_tickTimer != null)
                {
                    _tickTimer.Dispose();
                    _tickTimer = null;
                }
            }
        }

        /// <summary>
        /// PHASE 3: Timer callback: calls Engine.Tick() with current UTC time.
        /// Must be thread-safe, non-blocking, and never throw exceptions.
        /// </summary>
        private void TimerCallback(object? state)
        {
            try
            {
                if (_engine != null)
                {
                    var utcNow = DateTimeOffset.UtcNow;
                    _engine.Tick(utcNow);
                }
            }
            catch (Exception ex)
            {
                // Log but never throw - timer callbacks must not throw exceptions
                Log($"ERROR in tick timer callback: {ex.Message}", LogLevel.Error);
            }
        }

        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs connectionStatusUpdate)
        {
            if (_engine is null) return;
            
            // Forward connection status to health monitor using helper method and accessor
            var connectionName = connectionStatusUpdate.Connection?.Options?.Name ?? "Unknown";
            // ConnectionStatusEventArgs - pass the Connection.Status directly (NinjaTrader API)
            // The Connection property has a Status property of type NinjaTrader.Cbi.ConnectionStatus
            var connection = connectionStatusUpdate.Connection;
            var ntStatus = connection?.Status;
            // Fully qualify ConnectionStatus to avoid ambiguity between QTSW2.Robot.Core.ConnectionStatus and NinjaTrader.Cbi.ConnectionStatus
            var healthMonitorStatus = ntStatus != null ? ntStatus.ToHealthMonitorStatus() : QTSW2.Robot.Core.ConnectionStatus.ConnectionError;
            
            // Use RobotEngine's OnConnectionStatusUpdate method (replaces reflection)
            _engine.OnConnectionStatusUpdate(healthMonitorStatus, connectionName);
        }
    }
}

