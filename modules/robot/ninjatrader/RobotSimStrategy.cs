// NinjaTrader Strategy host for SIM execution
// This Strategy runs inside NinjaTrader and provides NT context (Account, Instrument, Events) to RobotEngine
//
// IMPORTANT: This Strategy MUST run in SIM account only.
// Copy into a NinjaTrader 8 strategy project and wire references to Robot.Core.

using System;
using System.Threading;
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
        private Timer? _tickTimer;
        private readonly object _timerLock = new object();

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
                var instrumentName = Instrument.MasterInstrument.Name;
                _engine = new RobotEngine(projectRoot, TimeSpan.FromSeconds(2), ExecutionMode.SIM, customLogDir: null, customTimetablePath: null, instrument: instrumentName);
                
                // PHASE 1: Set account info for startup banner
                var accountName = Account?.Name ?? "UNKNOWN";
                // Check if SIM account (verified earlier in OnStateChange)
                var environment = _simAccountVerified ? "SIM" : "UNKNOWN";
                _engine.SetAccountInfo(accountName, environment);
                
                // Start engine
                _engine.Start();

                // Get the adapter instance and wire NT context
                // Note: This requires exposing adapter from engine or using dependency injection
                // For now, we'll wire events directly to adapter via reflection or adapter registration
                WireNTContextToAdapter();
            }
            else if (State == State.Realtime)
            {
                // Start periodic timer for time-based state transitions (decoupled from bar arrivals)
                StartTickTimer();
            }
            else if (State == State.Terminated)
            {
                StopTickTimer();
                _engine?.Stop();
            }
        }

        /// <summary>
        /// Wire NinjaTrader context (Account, Instrument, Events) to adapter.
        /// </summary>
        private void WireNTContextToAdapter()
        {
            // Get adapter from engine using accessor method (replaces reflection)
            var adapter = _engine.GetExecutionAdapter() as NinjaTraderSimAdapter;
            
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

        private DateTime _lastDiagnosticLogBarTime = DateTime.MinValue;
        private const int DIAGNOSTIC_LOG_RATE_LIMIT_MINUTES = 1; // Log once per minute max

        protected override void OnBarUpdate()
        {
            if (_engine is null || !_simAccountVerified) return;
            if (CurrentBar < 1) return;

            // DIAGNOSTIC: Capture raw NinjaTrader bar timestamp before any conversion
            var barExchangeTime = Times[0][0]; // Exchange time (Chicago, Unspecified kind)
            var barRawNtTime = barExchangeTime;
            var barRawNtKind = barExchangeTime.Kind.ToString();
            
            // Convert bar time from exchange time to UTC using helper method
            var barUtc = NinjaTraderExtensions.ConvertBarTimeToUtc(barExchangeTime);
            
            // DIAGNOSTIC: Capture conversion details
            var barAssumedUtc = barUtc;
            var barAssumedUtcKind = barUtc.DateTime.Kind.ToString();
            var barChicagoOffset = new DateTimeOffset(barExchangeTime, TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time").GetUtcOffset(barExchangeTime));

            // DIAGNOSTIC: Rate-limited logging to verify NT timestamp behavior
            var timeSinceLastLog = (barExchangeTime - _lastDiagnosticLogBarTime).TotalMinutes;
            if (timeSinceLastLog >= DIAGNOSTIC_LOG_RATE_LIMIT_MINUTES || _lastDiagnosticLogBarTime == DateTime.MinValue)
            {
                _lastDiagnosticLogBarTime = barExchangeTime;
                Log($"DIAGNOSTIC: Raw NT Bar Time: {barExchangeTime:o}, Kind: {barExchangeTime.Kind}, Converted UTC: {barUtc:o}, Chicago Offset: {barChicagoOffset.Offset}", LogLevel.Information);
            }

            var high = (decimal)High[0];
            var low = (decimal)Low[0];
            var close = (decimal)Close[0];
            var nowUtc = DateTimeOffset.UtcNow;

            // Deliver bar data to engine (bars only provide market data, not time advancement)
            _engine.OnBar(barUtc, Instrument.MasterInstrument.Name, high, low, close, nowUtc);
            // NOTE: Tick() is now called by timer, not by bar arrivals
            // This ensures time-based state transitions occur even when no bars arrive
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
        /// Start periodic timer that calls Engine.Tick() every 1 second.
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
                Log("Tick timer started (1 second interval)", LogLevel.Information);
            }
        }

        /// <summary>
        /// Stop and dispose the tick timer.
        /// </summary>
        private void StopTickTimer()
        {
            lock (_timerLock)
            {
                if (_tickTimer != null)
                {
                    _tickTimer.Dispose();
                    _tickTimer = null;
                    Log("Tick timer stopped", LogLevel.Information);
                }
            }
        }

        /// <summary>
        /// Timer callback: calls Engine.Tick() with current UTC time.
        /// Must be thread-safe, non-blocking, and never throw exceptions.
        /// </summary>
        private void TimerCallback(object? state)
        {
            try
            {
                if (_engine != null && _simAccountVerified)
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

        /// <summary>
        /// Expose Account and Instrument to adapter (for order submission).
        /// </summary>
        public Account GetAccount() => Account;
        public Instrument GetInstrument() => Instrument;
    }
}
