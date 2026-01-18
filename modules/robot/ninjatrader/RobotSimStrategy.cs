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
                IsInstantiatedOnEachOptimizationIteration = false; // SIM mode only - no playback support
            }
            else if (State == State.DataLoaded)
            {
                // Verify SIM account only (playback mode not supported)
                if (Account is null)
                {
                    Log($"ERROR: Account is null. Aborting.", LogLevel.Error);
                    return;
                }

                if (!Account.IsSimAccount)
                {
                    Log($"ERROR: Account '{Account.Name}' is not a Sim account. Aborting.", LogLevel.Error);
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
                var environment = _simAccountVerified ? "SIM" : "UNKNOWN";
                _engine.SetAccountInfo(accountName, environment);
                
                // Start engine
                _engine.Start();

                // CRITICAL: Verify engine startup succeeded before requesting bars
                // Check that trading date is locked and streams are created
                var tradingDateStr = _engine.GetTradingDate();
                if (string.IsNullOrEmpty(tradingDateStr))
                {
                    var errorMsg = "CRITICAL: Engine started but trading date not locked. " +
                                 "This indicates timetable was invalid or missing trading_date. " +
                                 "Cannot request historical bars without trading date. " +
                                 "Check logs for TIMETABLE_INVALID or TIMETABLE_MISSING_TRADING_DATE events.";
                    Log(errorMsg, LogLevel.Error);
                    throw new InvalidOperationException(errorMsg);
                }

                // Request historical bars from NinjaTrader for pre-hydration (SIM mode)
                // This will throw if BarsRequest fails (fail-fast for critical errors)
                RequestHistoricalBarsForPreHydration();

                // Get the adapter instance and wire NT context
                // Note: This requires exposing adapter from engine or using dependency injection
                // For now, we'll wire events directly to adapter via reflection or adapter registration
                WireNTContextToAdapter();
            }
            else if (State == State.Realtime)
            {
                // CRITICAL: Verify engine is ready before starting tick timer
                // Check that trading date is locked and streams exist
                if (_engine == null)
                {
                    Log("ERROR: Cannot start tick timer - engine is null", LogLevel.Error);
                    return;
                }
                
                var tradingDateStr = _engine.GetTradingDate();
                if (string.IsNullOrEmpty(tradingDateStr))
                {
                    Log("ERROR: Cannot start tick timer - trading date not locked. Engine may be in StandDown state.", LogLevel.Error);
                    return;
                }
                
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
        /// Request historical bars from NinjaTrader using BarsRequest API for pre-hydration.
        /// Called after engine.Start() when streams are created and trading date is locked.
        /// </summary>
        private void RequestHistoricalBarsForPreHydration()
        {
            if (_engine == null || Instrument == null) return;

            try
            {
                // CRITICAL: Verify trading date is locked before requesting bars
                // This should not happen in normal operation - if it does, it's a configuration error
                var tradingDateStr = _engine.GetTradingDate();
                if (string.IsNullOrEmpty(tradingDateStr))
                {
                    var errorMsg = "CRITICAL: Cannot request historical bars - trading date not yet locked from timetable. " +
                                 $"This indicates a configuration error: timetable missing or invalid trading_date. " +
                                 $"Engine.Start() should have locked the trading date. Cannot proceed.";
                    Log(errorMsg, LogLevel.Error);
                    throw new InvalidOperationException(errorMsg);
                }

                // Parse trading date
                if (!DateOnly.TryParse(tradingDateStr, out var tradingDate))
                {
                    Log($"Invalid trading date format: {tradingDateStr}", LogLevel.Warning);
                    return;
                }

                // CRITICAL: Get range start and slot times from engine spec (not hardcoded)
                // Hardcoding creates silent misalignment when:
                // - Session definitions change
                // - Instruments differ
                // - S2 or other regimes are added
                // This must read from the same spec object that defines the stream
                var instrumentName = Instrument.MasterInstrument.Name.ToUpperInvariant();
                var sessionName = "S1"; // Default session - could be made configurable
                
                // CRITICAL: Get session info from engine spec - fail if not available
                // Hardcoded fallback defeats the purpose of reading from spec and creates silent misalignment
                var sessionInfo = _engine.GetSessionInfo(instrumentName, sessionName);
                
                if (!sessionInfo.HasValue)
                {
                    var errorMsg = $"Cannot get session info from spec for {instrumentName}/{sessionName}. " +
                                 $"This indicates a configuration error: instrument or session not found in spec. " +
                                 $"Cannot proceed with BarsRequest as time range would be incorrect.";
                    Log(errorMsg, LogLevel.Error);
                    throw new InvalidOperationException(errorMsg);
                }
                
                var (rangeStartTime, slotEndTimes) = sessionInfo.Value;
                var rangeStartChicago = rangeStartTime;
                
                if (string.IsNullOrWhiteSpace(rangeStartChicago))
                {
                    var errorMsg = $"Session {sessionName} has empty range_start_time in spec. Cannot proceed with BarsRequest.";
                    Log(errorMsg, LogLevel.Error);
                    throw new InvalidOperationException(errorMsg);
                }
                
                if (slotEndTimes == null || slotEndTimes.Count == 0)
                {
                    var errorMsg = $"Session {sessionName} has no slot_end_times in spec. Cannot proceed with BarsRequest.";
                    Log(errorMsg, LogLevel.Error);
                    throw new InvalidOperationException(errorMsg);
                }
                
                // Use first slot time from spec (or earliest if multiple streams exist)
                // Note: In a multi-stream scenario, we'd need to request bars for all slots
                // For now, use first slot time as a reasonable default
                var slotTimeChicago = slotEndTimes[0];
                
                Log($"Using session info from spec: range_start={rangeStartChicago}, first_slot={slotTimeChicago} (from {slotEndTimes.Count} slots)", LogLevel.Information);

                // CRITICAL: Only request bars up to "now" to avoid injecting future bars
                // If we request bars up to slotTimeChicago (07:30) but strategy starts at 07:25,
                // we'd get bars at 07:26, 07:27, etc. which would duplicate live bars
                //
                // RESTART BEHAVIOR POLICY: "Restart = Full Reconstruction"
                // When strategy restarts mid-session:
                // - BarsRequest loads historical bars from range_start to min(slot_time, now)
                // - If restart occurs after slot_time, only loads up to slot_time (not beyond)
                // - Range is recomputed from all available bars (historical + live)
                // - This ensures deterministic reconstruction but may differ from uninterrupted operation
                var timeService = new TimeService("America/Chicago");
                var nowUtc = DateTimeOffset.UtcNow;
                var nowChicago = timeService.ConvertUtcToChicago(nowUtc);
                var slotTimeChicagoTime = timeService.ConstructChicagoTime(tradingDate, slotTimeChicago);
                
                // Use the earlier of: slotTimeChicago or now (to avoid future bars)
                // CRITICAL: If restart occurs after slot_time, only request up to slot_time
                // This prevents loading bars beyond the range window, which would change the input set
                var endTimeChicago = nowChicago < slotTimeChicagoTime
                    ? nowChicago.ToString("HH:mm")
                    : slotTimeChicago;
                
                // Log restart detection if restarting after slot time
                if (nowChicago >= slotTimeChicagoTime)
                {
                    Log($"RESTART_POLICY: Restarting after slot time ({slotTimeChicago}) - BarsRequest limited to slot_time to prevent range input set changes", LogLevel.Information);
                }
                
                Log($"Requesting historical bars from NinjaTrader for {tradingDateStr} ({rangeStartChicago} to {endTimeChicago})", LogLevel.Information);

                // Request bars using helper class (only up to current time)
                List<Bar> bars;
                try
                {
                    bars = NinjaTraderBarRequest.RequestBarsForTradingDate(
                        Instrument,
                        tradingDate,
                        rangeStartChicago,
                        endTimeChicago,
                        timeService
                    );
                }
                catch (Exception ex)
                {
                    // BarsRequest failure is critical - log error and rethrow
                    var errorMsg = $"CRITICAL: Failed to request historical bars from NinjaTrader for {tradingDateStr} ({rangeStartChicago} to {endTimeChicago}). " +
                                 $"Error: {ex.Message}. " +
                                 $"Without historical bars, range computation may fail or be incomplete. " +
                                 $"Check NinjaTrader historical data availability and 'Days to load' setting.";
                    Log(errorMsg, LogLevel.Error);
                    throw new InvalidOperationException(errorMsg, ex);
                }

                // Validate bars were returned
                if (bars == null)
                {
                    var errorMsg = $"CRITICAL: BarsRequest returned null for {tradingDateStr} ({rangeStartChicago} to {endTimeChicago}). " +
                                 $"This indicates a NinjaTrader API failure. Cannot proceed.";
                    Log(errorMsg, LogLevel.Error);
                    throw new InvalidOperationException(errorMsg);
                }

                if (bars.Count == 0)
                {
                    // No bars returned - this may be acceptable if started after slot_time, but log as error for visibility
                    var errorMsg = $"WARNING: No historical bars returned from NinjaTrader for {tradingDateStr} ({rangeStartChicago} to {endTimeChicago}). " +
                                 $"Possible causes: " +
                                 $"1) Strategy started after slot_time (bars already passed), " +
                                 $"2) NinjaTrader 'Days to load' setting too low, " +
                                 $"3) No historical data available for this date. " +
                                 $"Range computation will rely on live bars only - may be incomplete.";
                    Log(errorMsg, LogLevel.Warning);
                    // Don't throw - allow degraded operation, but make it visible
                }
                else
                {
                    // Feed bars to engine for pre-hydration
                    // Note: Streams should exist by now (created in engine.Start())
                    _engine.LoadPreHydrationBars(Instrument.MasterInstrument.Name, bars, DateTimeOffset.UtcNow);
                    Log($"Loaded {bars.Count} historical bars from NinjaTrader for pre-hydration", LogLevel.Information);
                }
            }
            catch (Exception ex)
            {
                // Log error but don't fail - fallback to file-based or live bars
                Log($"Failed to request historical bars from NinjaTrader: {ex.Message}. Will use file-based or live bars.", LogLevel.Warning);
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

            var open = (decimal)Open[0];
            var high = (decimal)High[0];
            var low = (decimal)Low[0];
            var close = (decimal)Close[0];
            var nowUtc = DateTimeOffset.UtcNow;

            // Deliver bar data to engine (bars only provide market data, not time advancement)
            _engine.OnBar(barUtc, Instrument.MasterInstrument.Name, open, high, low, close, nowUtc);
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
                _tickTimer = new Timer(TickTimerCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
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
        private void TickTimerCallback(object? state)
        {
            try
            {
                // CRITICAL: Defensive checks before calling engine.Tick()
                if (_engine is null)
                {
                    Log("ERROR: Tick timer callback called but engine is null", LogLevel.Error);
                    return;
                }
                
                if (!_simAccountVerified)
                {
                    Log("ERROR: Tick timer callback called but SIM account not verified", LogLevel.Error);
                    return;
                }
                
                // Verify trading date is still locked (should never change, but defensive check)
                var tradingDateStr = _engine.GetTradingDate();
                if (string.IsNullOrEmpty(tradingDateStr))
                {
                    Log("ERROR: Tick timer callback called but trading date not locked. Engine may be in StandDown state.", LogLevel.Error);
                    return;
                }
                
                var utcNow = DateTimeOffset.UtcNow;
                _engine.Tick(utcNow);
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
