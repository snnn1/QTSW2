// NinjaTrader Heartbeat Strategy - Authoritative Engine Liveness Signal
// This Strategy emits ENGINE_HEARTBEAT events every 5 seconds using a wall-clock timer.
//
// IMPORTANT: This Strategy is completely independent of:
// - bars
// - ticks
// - instruments
// - trading logic
// - market data availability
//
// This strategy answers one question only: "Is NinjaTrader alive and executing code?"
//
// Copy into a NinjaTrader 8 strategy project and wire references to Robot.Core.

using System;
using System.Collections.Generic;
using System.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using QTSW2.Robot.Core;

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Heartbeat Strategy: Emits ENGINE_HEARTBEAT events every 5 seconds as a pure liveness signal.
    /// This strategy is independent of market data, bars, ticks, instruments, and trading logic.
    /// </summary>
    public class HeartbeatStrategy : Strategy
    {
        private Timer? _heartbeatTimer;
        private readonly object _timerLock = new object();
        private RobotLogger? _logger;
        private readonly string _instanceId;
        private const int HEARTBEAT_INTERVAL_SECONDS = 5;

        /// <summary>
        /// Constructor: Generate unique instance ID for this strategy instance.
        /// </summary>
        public HeartbeatStrategy()
        {
            // Generate unique GUID-based instance ID
            var guidStr = Guid.NewGuid().ToString("N");
            _instanceId = guidStr.Substring(0, 8);
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "HeartbeatStrategy";
                Calculate = Calculate.OnBarClose; // Required by NinjaTrader, but never used
            }
            else if (State == State.DataLoaded)
            {
                // Initialize RobotLogger (no RobotEngine dependency)
                try
                {
                    var projectRoot = ProjectRootResolver.ResolveProjectRoot();
                    // Pass instrument: null to create ENGINE-level logger
                    _logger = new RobotLogger(projectRoot, customLogDir: null, instrument: null);
                }
                catch (Exception ex)
                {
                    // Log error but don't fail - strategy can continue without logger
                    Log($"WARNING: Failed to initialize RobotLogger: {ex.Message}. Heartbeat events will not be emitted.", LogLevel.Warning);
                }
            }
            else if (State == State.Realtime)
            {
                // Start heartbeat timer when strategy enters realtime state
                StartHeartbeatTimer();
            }
            else if (State == State.Terminated)
            {
                // Stop and dispose timer when strategy terminates
                StopHeartbeatTimer();
            }
        }

        /// <summary>
        /// Start periodic heartbeat timer that fires every 5 seconds.
        /// Thread-safe implementation using lock pattern.
        /// </summary>
        private void StartHeartbeatTimer()
        {
            lock (_timerLock)
            {
                if (_heartbeatTimer != null)
                {
                    // Timer already started, ignore
                    return;
                }

                // Create timer that fires every 5 seconds
                // Timer callback must be thread-safe and never throw
                _heartbeatTimer = new Timer(TimerCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(HEARTBEAT_INTERVAL_SECONDS));
            }
        }

        /// <summary>
        /// Stop and dispose the heartbeat timer.
        /// Thread-safe implementation using lock pattern.
        /// </summary>
        private void StopHeartbeatTimer()
        {
            lock (_timerLock)
            {
                if (_heartbeatTimer != null)
                {
                    _heartbeatTimer.Dispose();
                    _heartbeatTimer = null;
                }
            }
        }

        /// <summary>
        /// Timer callback: Emit ENGINE_HEARTBEAT event.
        /// Must be thread-safe, non-blocking, and never throw exceptions.
        /// </summary>
        private void TimerCallback(object? state)
        {
            try
            {
                if (_logger == null)
                {
                    // Logger not initialized - skip heartbeat (already logged warning in OnStateChange)
                    return;
                }

                var utcNow = DateTimeOffset.UtcNow;

                // Build heartbeat event data
                var eventData = new Dictionary<string, object?>
                {
                    { "instance_id", _instanceId },
                    { "strategy_state", State.ToString() },
                    { "ninjatrader_connection_state", GetConnectionState() },
                    { "last_bar_utc", null },
                    { "last_tick_utc", null }
                };

                // Construct ENGINE_HEARTBEAT event using RobotEvents.EngineBase()
                var heartbeatEvent = RobotEvents.EngineBase(
                    utcNow: utcNow,
                    tradingDate: "", // Empty - no trading date needed for heartbeat
                    eventType: "ENGINE_HEARTBEAT",
                    state: "ENGINE",
                    extra: eventData
                );

                // Write heartbeat event via RobotLogger
                _logger.Write(heartbeatEvent);
            }
            catch (Exception ex)
            {
                // Log but never throw - timer callbacks must not throw exceptions
                // Use NinjaTrader Log method as fallback since RobotLogger might be unavailable
                try
                {
                    Log($"ERROR in heartbeat timer callback: {ex.Message}", LogLevel.Error);
                }
                catch
                {
                    // If even Log() fails, silently swallow (timer callback must never throw)
                }
            }
        }

        /// <summary>
        /// Get NinjaTrader connection state as a string.
        /// Gracefully handles null Connection and exceptions.
        /// Returns "Connected", "Disconnected", or "Unknown".
        /// </summary>
        private string GetConnectionState()
        {
            try
            {
                // Attempt to access Connection via NinjaTrader.Cbi.Connection static class
                // Note: Connection status may not be directly accessible in all contexts
                // This is a best-effort attempt - if unavailable, returns "Unknown"
                var connections = NinjaTrader.Cbi.Connection.Connections;
                if (connections != null && connections.Count > 0)
                {
                    // Check first connection status (most strategies use single connection)
                    var connection = connections[0];
                    if (connection != null)
                    {
                        // Fully qualify ConnectionStatus to avoid ambiguity with QTSW2.Robot.Core.ConnectionStatus
                        var status = connection.Status;
                        if (status == NinjaTrader.Cbi.ConnectionStatus.Connected)
                        {
                            return "Connected";
                        }
                        else if (status == NinjaTrader.Cbi.ConnectionStatus.Disconnected)
                        {
                            return "Disconnected";
                        }
                    }
                }

                // Connection not available or status unknown
                return "Unknown";
            }
            catch (Exception)
            {
                // Gracefully handle any exceptions - never crash
                // Connection state detection is optional - return "Unknown" if unavailable
                return "Unknown";
            }
        }
    }
}
