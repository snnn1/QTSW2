// NinjaTrader Strategy host for SIM execution
// This Strategy runs inside NinjaTrader and provides NT context (Account, Instrument, Events) to RobotEngine
//
// IMPORTANT: This Strategy MUST run in SIM account only.
// Copy into a NinjaTrader 8 strategy project and wire references to Robot.Core.
//
// REQUIRED FILES: When copying this file to NT project, also include:
// - NinjaTraderBarRequest.cs (from modules/robot/ninjatrader/)
// - Robot.Core.dll (or source files from modules/robot/core/)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        private bool _engineReady = false; // Single latch: true once engine is fully initialized and ready
        private Timer? _tickTimer;
        private readonly object _timerLock = new object();
        
        // Rate-limiting for timezone detection logging (per instrument)
        private readonly Dictionary<string, DateTimeOffset> _lastTimezoneDetectionLogUtc = new Dictionary<string, DateTimeOffset>();
        
        // CRITICAL FIX: Lock bar time interpretation after first detection
        private enum BarTimeInterpretation { UTC, Chicago }
        private BarTimeInterpretation? _barTimeInterpretation = null;
        private bool _barTimeInterpretationLocked = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "RobotSimStrategy";
                Calculate = Calculate.OnBarClose; // Bar-close series
                IsUnmanaged = true; // Required for manual order management
                IsInstantiatedOnEachOptimizationIteration = false; // SIM mode only
            }
            else if (State == State.DataLoaded)
            {
                // Verify SIM account only
                if (Account is null)
                {
                    Log($"ERROR: Account is null. Aborting.", LogLevel.Error);
                    return;
                }

                // Check if account is SIM account by checking account name pattern
                // Note: NinjaTrader Account class doesn't have IsSimAccount property
                // SIM accounts typically have names like "Sim101", "Simulation", "DEMO123", etc.
                var accountName = Account?.Name ?? "";
                var accountNameUpper = accountName.ToUpperInvariant();
                var isSimAccount = accountNameUpper.Contains("SIM") || 
                                 accountNameUpper.Contains("SIMULATION") ||
                                 accountNameUpper.Contains("DEMO");
                
                if (!isSimAccount)
                {
                    Log($"ERROR: Account '{Account?.Name}' does not appear to be a Sim account. Aborting.", LogLevel.Error);
                    return;
                }

                _simAccountVerified = true;
                Log($"SIM account verified: {Account.Name}", LogLevel.Information);

                // Initialize RobotEngine in SIM mode
                var projectRoot = ProjectRootResolver.ResolveProjectRoot();
                var instrumentName = Instrument.MasterInstrument.Name;
                _engine = new RobotEngine(projectRoot, TimeSpan.FromSeconds(2), ExecutionMode.SIM, customLogDir: null, customTimetablePath: null, instrument: instrumentName);
                
                // PHASE 1: Set account info for startup banner
                // Reuse accountName variable declared above
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
                
                // ENGINE_READY latch: Set once when all initialization is complete
                // This flag guards all execution paths to simplify reasoning and reduce repetition
                _engineReady = true;
                Log("Engine ready - all initialization complete", LogLevel.Information);
            }
            else if (State == State.Realtime)
            {
                // CRITICAL: Verify engine is ready before starting tick timer
                // Use ENGINE_READY latch to simplify condition checks
                if (!_engineReady)
                {
                    Log("ERROR: Cannot start tick timer - engine not ready. Engine may not be fully initialized.", LogLevel.Error);
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

                // CRITICAL: Get time range covering ALL enabled streams for this instrument
                // This ensures S1 (02:00) and S2 (08:00) streams both get their historical bars
                // The range covers from earliest range_start to latest slot_time across all enabled streams
                var instrumentName = Instrument.MasterInstrument.Name.ToUpperInvariant();
                var timeRange = _engine.GetBarsRequestTimeRange(instrumentName);
                
                if (!timeRange.HasValue)
                {
                    var errorMsg = $"Cannot determine BarsRequest time range for {instrumentName}. " +
                                 $"This indicates no enabled streams exist for this instrument, or streams not yet created. " +
                                 $"Ensure timetable has enabled streams for {instrumentName} and engine.Start() completed successfully.";
                    Log(errorMsg, LogLevel.Error);
                    throw new InvalidOperationException(errorMsg);
                }
                
                var (rangeStartChicago, slotTimeChicago) = timeRange.Value;
                
                if (string.IsNullOrWhiteSpace(rangeStartChicago) || string.IsNullOrWhiteSpace(slotTimeChicago))
                {
                    var errorMsg = $"Invalid BarsRequest time range for {instrumentName}: range_start={rangeStartChicago}, slot_time={slotTimeChicago}. " +
                                 $"Cannot proceed with BarsRequest.";
                    Log(errorMsg, LogLevel.Error);
                    throw new InvalidOperationException(errorMsg);
                }
                
                Log($"Using BarsRequest time range covering all enabled streams: range_start={rangeStartChicago}, latest_slot={slotTimeChicago}", LogLevel.Information);

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
                var rangeStartChicagoTime = timeService.ConstructChicagoTime(tradingDate, rangeStartChicago);
                var nowChicagoDate = DateOnly.FromDateTime(nowChicago.DateTime);
                
                // Use the earlier of: slotTimeChicago or now (to avoid future bars)
                // CRITICAL: Only use current time if we're on the trading date AND before slot_time
                // If we're on a different date (e.g., requesting tomorrow's bars), always use slot_time
                // This prevents loading bars beyond the range window, which would change the input set
                var endTimeChicago = (nowChicagoDate == tradingDate && nowChicago < slotTimeChicagoTime)
                    ? nowChicago.ToString("HH:mm")
                    : slotTimeChicago;
                
                // Check if we're starting before range_start_time (request would be invalid)
                if (nowChicagoDate == tradingDate && nowChicago < rangeStartChicagoTime)
                {
                    var warningMsg = $"Starting before range_start_time ({rangeStartChicago}) - skipping BarsRequest. " +
                                   $"System will rely on live bars once range starts. Current time: {nowChicago:HH:mm}";
                    Log(warningMsg, LogLevel.Warning);
                    
                    // Log BarsRequest skipped event
                    if (_engine != null)
                    {
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_SKIPPED", new Dictionary<string, object>
                        {
                            { "instrument", instrumentName },
                            { "trading_date", tradingDateStr },
                            { "range_start_time", rangeStartChicago },
                            { "current_time_chicago", nowChicago.ToString("HH:mm") },
                            { "reason", "Starting before range_start_time" },
                            { "note", "System will rely on live bars once range starts" }
                        });
                    }
                    return; // Skip BarsRequest, rely on live bars
                }
                
                // Log restart detection if restarting after slot time (on same trading date)
                if (nowChicagoDate == tradingDate && nowChicago >= slotTimeChicagoTime)
                {
                    Log($"RESTART_POLICY: Restarting after slot time ({slotTimeChicago}) - BarsRequest limited to slot_time to prevent range input set changes", LogLevel.Information);
                }
                
                Log($"Requesting historical bars from NinjaTrader for {tradingDateStr} ({rangeStartChicago} to {endTimeChicago})", LogLevel.Information);
                
                // Log BarsRequest initialization event
                if (_engine != null)
                {
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_INITIALIZATION", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "trading_date", tradingDateStr },
                        { "range_start_time", rangeStartChicago },
                        { "slot_time", slotTimeChicago },
                        { "end_time", endTimeChicago },
                        { "current_time_chicago", nowChicago.ToString("HH:mm") },
                        { "is_restart_after_slot", nowChicagoDate == tradingDate && nowChicago >= slotTimeChicagoTime },
                        { "note", "BarsRequest initialization - requesting historical bars for pre-hydration" }
                    });
                }

                // Log intent before request
                var requestStartUtc = timeService.ConvertChicagoLocalToUtc(tradingDate, rangeStartChicago);
                var requestEndUtc = timeService.ConvertChicagoLocalToUtc(tradingDate, endTimeChicago);
                
                // Log BARSREQUEST_REQUESTED event
                try
                {
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_REQUESTED", new Dictionary<string, object>
                    {
                        { "instrument", Instrument.MasterInstrument.Name },
                        { "trading_date", tradingDateStr },
                        { "range_start_chicago", rangeStartChicago },
                        { "request_end_chicago", endTimeChicago },
                        { "start_utc", requestStartUtc.ToString("o") },
                        { "end_utc", requestEndUtc.ToString("o") },
                        { "bars_period", "1m" },
                        { "trading_hours_template", Instrument.MasterInstrument.TradingHours?.Name ?? "UNKNOWN" },
                        { "execution_mode", "SIM" }
                    });
                }
                catch
                {
                    // LogEngineEvent not available - log using NT Log method instead
                    Log($"BARSREQUEST_REQUESTED: {tradingDateStr} ({rangeStartChicago} to {endTimeChicago})", LogLevel.Information);
                }

                // Request bars using helper class (only up to current time)
                List<Bar> bars;
                try
                {
                    // Build log callback for BARSREQUEST_RAW_RESULT
                    Action<string, object>? logCallback = null;
                    try
                    {
                        logCallback = (eventType, data) =>
                        {
                            try
                            {
                                _engine.LogEngineEvent(DateTimeOffset.UtcNow, eventType, data);
                            }
                            catch { /* Ignore logging errors */ }
                        };
                    }
                    catch { /* Log callback optional */ }
                    
                    // Direct call to NinjaTraderBarRequest (same namespace)
                    bars = NinjaTraderBarRequest.RequestBarsForTradingDate(
                        Instrument,
                        tradingDate,
                        rangeStartChicago,
                        endTimeChicago,
                        timeService,
                        logCallback
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
                    
                    // Log BarsRequest unexpected count event
                    if (_engine != null)
                    {
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_UNEXPECTED_COUNT", new Dictionary<string, object>
                        {
                            { "instrument", instrumentName },
                            { "trading_date", tradingDateStr },
                            { "bars_returned", 0 },
                            { "expected_range", $"{rangeStartChicago} to {endTimeChicago}" },
                            { "current_time_chicago", nowChicago.ToString("HH:mm") },
                            { "reason", "No bars returned from BarsRequest" },
                            { "possible_causes", new[] { 
                                "Strategy started after slot_time (bars already passed)",
                                "NinjaTrader 'Days to load' setting too low",
                                "No historical data available for this date"
                            }},
                            { "note", "Range computation will rely on live bars only - may be incomplete" }
                        });
                    }
                    // Don't throw - allow degraded operation, but make it visible
                }
                else
                {
                    // Feed bars to engine for pre-hydration
                    // Note: Streams should exist by now (created in engine.Start())
                    _engine.LoadPreHydrationBars(Instrument.MasterInstrument.Name, bars, DateTimeOffset.UtcNow);
                    Log($"Loaded {bars.Count} historical bars from NinjaTrader for pre-hydration", LogLevel.Information);
                    
                    // Log successful BarsRequest with bar count check
                    if (_engine != null)
                    {
                        // Calculate expected bar count (rough estimate: 1 bar per minute)
                        var rangeStartTime = timeService.ConstructChicagoTime(tradingDate, rangeStartChicago);
                        var endTime = (nowChicagoDate == tradingDate && nowChicago < slotTimeChicagoTime)
                            ? nowChicago
                            : timeService.ConstructChicagoTime(tradingDate, endTimeChicago);
                        var expectedMinutes = (int)(endTime - rangeStartTime).TotalMinutes;
                        var expectedBarCount = Math.Max(0, expectedMinutes);
                        
                        // Log if count is unexpectedly low (less than 50% of expected)
                        if (bars.Count < expectedBarCount * 0.5 && expectedBarCount > 10)
                        {
                            _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_UNEXPECTED_COUNT", new Dictionary<string, object>
                            {
                                { "instrument", instrumentName },
                                { "trading_date", tradingDateStr },
                                { "bars_returned", bars.Count },
                                { "expected_bar_count", expectedBarCount },
                                { "expected_range", $"{rangeStartChicago} to {endTimeChicago}" },
                                { "coverage_percent", Math.Round((double)bars.Count / expectedBarCount * 100, 1) },
                                { "reason", "Bar count lower than expected" },
                                { "note", "BarsRequest returned fewer bars than expected - may indicate data gaps or timing issues" }
                            });
                        }
                    }
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

        protected override void OnBarUpdate()
        {
            // Use ENGINE_READY latch to guard execution
            if (!_engineReady || _engine is null) return;
            if (CurrentBar < 1) return;

            // CRITICAL FIX: NinjaTrader Times[0][0] timezone handling with locking
            // Lock interpretation after first detection to prevent mid-run flips
            var barExchangeTime = Times[0][0]; // Exchange time from NinjaTrader
            var nowUtc = DateTimeOffset.UtcNow;
            DateTimeOffset barUtc;
            
            if (!_barTimeInterpretationLocked)
            {
                // First bar: Detect and lock interpretation
                // Try treating Times[0][0] as UTC first
                var barUtcIfUtc = new DateTimeOffset(DateTime.SpecifyKind(barExchangeTime, DateTimeKind.Utc), TimeSpan.Zero);
                var barAgeIfUtc = (nowUtc - barUtcIfUtc).TotalMinutes;
                
                // Try treating Times[0][0] as Chicago time
                var barUtcIfChicago = NinjaTraderExtensions.ConvertBarTimeToUtc(barExchangeTime);
                var barAgeIfChicago = (nowUtc - barUtcIfChicago).TotalMinutes;
                
                // Choose interpretation that gives reasonable bar age (between 0 and 10 minutes for recent bars)
                string selectedInterpretation;
                string selectionReason;
                
                if (barAgeIfUtc >= 0 && barAgeIfUtc < 10 && barAgeIfUtc < barAgeIfChicago)
                {
                    // Times[0][0] appears to be UTC
                    _barTimeInterpretation = BarTimeInterpretation.UTC;
                    barUtc = barUtcIfUtc;
                    selectedInterpretation = "UTC";
                    selectionReason = $"UTC interpretation gives reasonable bar age ({barAgeIfUtc:F2} min) and is better than Chicago ({barAgeIfChicago:F2} min)";
                }
                else if (barAgeIfChicago >= 0 && barAgeIfChicago < 10)
                {
                    // Times[0][0] appears to be Chicago time
                    _barTimeInterpretation = BarTimeInterpretation.Chicago;
                    barUtc = barUtcIfChicago;
                    selectedInterpretation = "CHICAGO";
                    selectionReason = $"Chicago interpretation gives reasonable bar age ({barAgeIfChicago:F2} min)";
                }
                else
                {
                    // Fallback: Use Chicago interpretation (documented behavior)
                    _barTimeInterpretation = BarTimeInterpretation.Chicago;
                    barUtc = barUtcIfChicago;
                    selectedInterpretation = "CHICAGO";
                    selectionReason = $"Fallback to Chicago interpretation (documented behavior). UTC age: {barAgeIfUtc:F2} min, Chicago age: {barAgeIfChicago:F2} min";
                }
                
                // Lock interpretation
                _barTimeInterpretationLocked = true;
                
                // Log detection (always log on first bar)
                if (_engine != null)
                {
                    var instrumentName = Instrument.MasterInstrument.Name;
                    _engine.LogEngineEvent(nowUtc, "BAR_TIME_INTERPRETATION_DETECTED", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "raw_times_value", barExchangeTime.ToString("o") },
                        { "raw_times_kind", barExchangeTime.Kind.ToString() },
                        { "chosen_interpretation", selectedInterpretation },
                        { "reason", selectionReason },
                        { "bar_age_if_utc", Math.Round(barAgeIfUtc, 2) },
                        { "bar_age_if_chicago", Math.Round(barAgeIfChicago, 2) },
                        { "final_bar_timestamp_utc", barUtc.ToString("o") },
                        { "current_time_utc", nowUtc.ToString("o") }
                    });
                }
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
                    if (_engine != null)
                    {
                        var instrumentName = Instrument.MasterInstrument.Name;
                        _engine.LogEngineEvent(nowUtc, "BAR_TIME_INTERPRETATION_MISMATCH", new Dictionary<string, object>
                        {
                            { "instrument", instrumentName },
                            { "severity", "CRITICAL" },
                            { "locked_interpretation", _barTimeInterpretation.ToString() },
                            { "current_bar_age_minutes", Math.Round(barAge, 2) },
                            { "raw_bar_time", barExchangeTime.ToString("o") },
                            { "warning", "Bar time interpretation would flip - this should not happen" }
                        });
                    }
                }
            }

            var open = (decimal)Open[0];
            var high = (decimal)High[0];
            var low = (decimal)Low[0];
            var close = (decimal)Close[0];

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
                // CRITICAL: Use ENGINE_READY latch to guard execution
                // This simplifies reasoning and reduces repetition of multiple condition checks
                if (!_engineReady || _engine is null)
                {
                    // Log error only if engine was ready before (to avoid spam on startup)
                    if (_engineReady && _engine == null)
                    {
                        Log("ERROR: Tick timer callback called but engine became null after initialization", LogLevel.Error);
                    }
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
