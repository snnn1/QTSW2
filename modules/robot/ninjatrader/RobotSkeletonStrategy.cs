// NinjaTrader wrapper (thin adapter)
// IMPORTANT: This is a skeleton and MUST NEVER place orders.
//
// This file intentionally avoids being part of any build in this repo.
// Copy into a NinjaTrader 8 strategy project and wire references to the core engine.

using System;
using System.Collections.Generic;
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
        
        // CRITICAL FIX: Lock bar time interpretation after first detection
        private enum BarTimeInterpretation { UTC, Chicago }
        private BarTimeInterpretation? _barTimeInterpretation = null;
        private bool _barTimeInterpretationLocked = false;

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

            // CRITICAL FIX: NinjaTrader Times[0][0] timezone handling with locking
            // Lock interpretation after first detection to prevent mid-run flips
            var barExchangeTime = Times[0][0]; // Exchange time from NinjaTrader
            var nowUtc = DateTimeOffset.UtcNow;
            DateTimeOffset barUtc;
            
            if (!_barTimeInterpretationLocked)
            {
                // First bar: Detect and lock interpretation
                // SIMPLIFIED: We know Times[0][0] is UTC for live bars, so try UTC first
                var barUtcIfUtc = new DateTimeOffset(DateTime.SpecifyKind(barExchangeTime, DateTimeKind.Utc), TimeSpan.Zero);
                var barAgeIfUtc = (nowUtc - barUtcIfUtc).TotalMinutes;
                
                string selectedInterpretation;
                string selectionReason;
                
                // If UTC gives reasonable age (0-60 min), use it (expected case for live bars)
                if (barAgeIfUtc >= 0 && barAgeIfUtc <= 60)
                {
                    _barTimeInterpretation = BarTimeInterpretation.UTC;
                    barUtc = barUtcIfUtc;
                    selectedInterpretation = "UTC";
                    selectionReason = $"UTC interpretation gives reasonable bar age ({barAgeIfUtc:F2} min)";
                }
                else
                {
                    // Edge case: UTC didn't work, try Chicago (for historical bars or edge cases)
                    var barUtcIfChicago = NinjaTraderExtensions.ConvertBarTimeToUtc(barExchangeTime);
                    var barAgeIfChicago = (nowUtc - barUtcIfChicago).TotalMinutes;
                    
                    if (barAgeIfChicago >= 0 && barAgeIfChicago <= 60)
                    {
                        _barTimeInterpretation = BarTimeInterpretation.Chicago;
                        barUtc = barUtcIfChicago;
                        selectedInterpretation = "CHICAGO";
                        selectionReason = $"Chicago interpretation gives reasonable bar age ({barAgeIfChicago:F2} min) - UTC gave {barAgeIfUtc:F2} min";
                    }
                    else
                    {
                        // Both failed - default to UTC (we know live bars are UTC)
                        _barTimeInterpretation = BarTimeInterpretation.UTC;
                        barUtc = barUtcIfUtc;
                        selectedInterpretation = "UTC";
                        selectionReason = $"Both interpretations unreasonable (UTC: {barAgeIfUtc:F2} min, Chicago: {barAgeIfChicago:F2} min) - defaulting to UTC for live bars";
                    }
                }
                
                // Lock interpretation
                _barTimeInterpretationLocked = true;
                
                // CRITICAL INVARIANT LOG: Explicit log right after locking to make it impossible to miss
                // This ensures if interpretation is wrong, we know immediately in logs (seconds, not hours)
                var finalBarAge = (nowUtc - barUtc).TotalMinutes;
                var invariantMessage = $"Bar time interpretation LOCKED = {selectedInterpretation}. First bar age = {Math.Round(finalBarAge, 2)} minutes. Reason = {selectionReason}";
                
                // Log to engine if available
                if (_engine != null)
                {
                    var instrumentName = Instrument.MasterInstrument.Name;
                    _engine.LogEngineEvent(nowUtc, "BAR_TIME_INTERPRETATION_LOCKED", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "locked_interpretation", selectedInterpretation },
                        { "reason", selectionReason },
                        { "first_bar_age_minutes", Math.Round(finalBarAge, 2) },
                        { "bar_age_if_utc", Math.Round(barAgeIfUtc, 2) },
                        { "raw_times_value", barExchangeTime.ToString("o") },
                        { "raw_times_kind", barExchangeTime.Kind.ToString() },
                        { "final_bar_timestamp_utc", barUtc.ToString("o") },
                        { "current_time_utc", nowUtc.ToString("o") },
                        { "invariant", invariantMessage }
                    });
                }
                
                // Also log to NinjaTrader console for visibility
                Log(invariantMessage, LogLevel.Information);
            }
            else
            {
                // Subsequent bars: Use locked interpretation and verify consistency
                if (_barTimeInterpretation == BarTimeInterpretation.UTC)
                {
                    barUtc = new DateTimeOffset(DateTime.SpecifyKind(barExchangeTime, DateTimeKind.Utc), TimeSpan.Zero);
                }
                else
                {
                    barUtc = NinjaTraderExtensions.ConvertBarTimeToUtc(barExchangeTime);
                }
                
                // Verify locked interpretation still gives valid bar age
                var barAge = (nowUtc - barUtc).TotalMinutes;
                if (barAge < 0 || barAge > 60)
                {
                    // CRITICAL: Interpretation would flip - log alert
                    // Note: Skeleton strategy doesn't have full logging, but we can log to console
                    Log($"CRITICAL: Bar time interpretation mismatch. Locked: {_barTimeInterpretation}, Age: {barAge:F2} min", LogLevel.Error);
                }
            }

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

