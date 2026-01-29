// NinjaTrader Heartbeat AddOn - Authoritative Engine Liveness Signal
// This AddOn emits ENGINE_HEARTBEAT events every 5 seconds using a wall-clock timer.
//
// IMPORTANT: This AddOn is completely independent of:
// - bars
// - ticks
// - instruments
// - trading logic
// - charts
// - market data availability
//
// This AddOn answers one question only: "Is NinjaTrader alive and executing code?"
// It runs automatically when NinjaTrader starts - no chart needed!
//
// Copy into a NinjaTrader 8 project and wire references to Robot.Core.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using QTSW2.Robot.Core;

namespace NinjaTrader.NinjaScript
{
    /// <summary>
    /// Heartbeat AddOn: Emits ENGINE_HEARTBEAT events every 5 seconds as a pure liveness signal.
    /// This AddOn runs automatically when NinjaTrader starts - no chart or strategy needed.
    /// </summary>
    public class HeartbeatAddOn : NinjaTrader.NinjaScript.AddOnBase
    {
        private Timer? _heartbeatTimer;
        private readonly object _timerLock = new object();
        private RobotLogger? _logger;
        private readonly string _instanceId;
        private const int HEARTBEAT_INTERVAL_SECONDS = 5;

        /// <summary>
        /// Constructor: Generate unique instance ID for this AddOn instance.
        /// </summary>
        public HeartbeatAddOn()
        {
            // Generate unique GUID-based instance ID
            var guidStr = Guid.NewGuid().ToString("N");
            _instanceId = guidStr.Substring(0, 8);
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "HeartbeatAddOn";
                Description = "Emits ENGINE_HEARTBEAT events every 5 seconds for Watchdog liveness monitoring";
            }
            else if (State == State.Configure)
            {
                Print("HeartbeatAddOn CONFIGURE");
                // Initialize RobotLogger when AddOn is configured
                try
                {
                    var projectRoot = ProjectRootResolver.ResolveProjectRoot();
                    Print($"HeartbeatAddOn: Project root resolved: {projectRoot}");
                    // Pass instrument: null to create ENGINE-level logger
                    _logger = new RobotLogger(projectRoot, customLogDir: null, instrument: null);
                    Print("HeartbeatAddOn: RobotLogger initialized successfully");
                    
                    // Test write to verify logger works
                    try
                    {
                        var testEvent = RobotEvents.EngineBase(
                            DateTimeOffset.UtcNow,
                            tradingDate: "",
                            eventType: "HEARTBEAT_ADDON_STARTED",
                            state: "ENGINE",
                            extra: new Dictionary<string, object?> { { "instance_id", _instanceId } }
                        );
                        _logger.Write(testEvent);
                        Print("HeartbeatAddOn: Test event written successfully");
                    }
                    catch (Exception testEx)
                    {
                        Print($"HeartbeatAddOn: ERROR writing test event: {testEx.Message}");
                    }
                }
                catch (Exception ex)
                {
                    // Log error but don't fail - AddOn can continue without logger
                    Print($"WARNING: Failed to initialize RobotLogger: {ex.Message}. Heartbeat events will not be emitted.");
                    Print($"HeartbeatAddOn: Exception type: {ex.GetType().Name}");
                    if (ex.InnerException != null)
                    {
                        Print($"HeartbeatAddOn: Inner exception: {ex.InnerException.Message}");
                    }
                }
            }
            else if (State == State.Active)
            {
                Print("HeartbeatAddOn ACTIVE");
                // Start heartbeat timer when AddOn becomes active
                StartHeartbeatTimer();
            }
            else if (State == State.Terminated)
            {
                // Stop and dispose timer when AddOn terminates
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
                    Print("HeartbeatAddOn: Timer already started, ignoring");
                    return;
                }

                // Create timer that fires every 5 seconds
                // Timer callback must be thread-safe and never throw
                _heartbeatTimer = new Timer(TimerCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(HEARTBEAT_INTERVAL_SECONDS));
                Print($"HeartbeatAddOn: Timer started (interval: {HEARTBEAT_INTERVAL_SECONDS} seconds)");
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
                    Print("HeartbeatAddOn: Logger is null - skipping heartbeat");
                    return;
                }

                var utcNow = DateTimeOffset.UtcNow;

                // Build heartbeat event data
                var eventData = new Dictionary<string, object?>
                {
                    { "instance_id", _instanceId },
                    { "addon_state", State.ToString() },
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
                try
                {
                    // Debug: Check event structure before writing
                    if (heartbeatEvent is Dictionary<string, object?> evtDict)
                    {
                        var eventType = evtDict.TryGetValue("event_type", out var et) ? et?.ToString() ?? "MISSING" : "MISSING";
                        var stream = evtDict.TryGetValue("stream", out var st) ? st?.ToString() ?? "MISSING" : "MISSING";
                        Print($"HeartbeatAddOn: Event structure - event_type: {eventType}, stream: {stream}");
                    }
                    
                    _logger.Write(heartbeatEvent);
                    Print($"HeartbeatAddOn: Heartbeat emitted at {utcNow:HH:mm:ss} UTC - Write() call completed");
                }
                catch (Exception writeEx)
                {
                    Print($"HeartbeatAddOn: ERROR writing heartbeat event: {writeEx.Message}");
                    Print($"HeartbeatAddOn: Exception type: {writeEx.GetType().Name}");
                    if (writeEx.InnerException != null)
                    {
                        Print($"HeartbeatAddOn: Inner exception: {writeEx.InnerException.Message}");
                    }
                    var stackTrace = writeEx.StackTrace ?? "";
                    var traceLength = stackTrace.Length > 500 ? 500 : stackTrace.Length;
                    Print($"HeartbeatAddOn: Stack trace: {stackTrace.Substring(0, traceLength)}");
                }
            }
            catch (Exception ex)
            {
                // Log but never throw - timer callbacks must not throw exceptions
                // Use NinjaTrader Print method as fallback since RobotLogger might be unavailable
                try
                {
                    Print($"ERROR in heartbeat timer callback: {ex.Message}");
                }
                catch
                {
                    // If even Print() fails, silently swallow (timer callback must never throw)
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
                    // Check first connection status (most setups use single connection)
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
