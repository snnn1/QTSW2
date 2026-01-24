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
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;
using CoreBar = QTSW2.Robot.Core.Bar; // Alias to avoid ambiguity with NinjaTrader.Data.Bar

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
        
        // Heartbeat watchdog: Track last successful Tick() call to detect timer failures
        private DateTimeOffset _lastSuccessfulTickUtc = DateTimeOffset.MinValue;
        private const int HEARTBEAT_WATCHDOG_THRESHOLD_SECONDS = 90; // Alert if no Tick() in 90 seconds
        
        // Rate-limiting for timezone detection logging (per instrument)
        private readonly Dictionary<string, DateTimeOffset> _lastTimezoneDetectionLogUtc = new Dictionary<string, DateTimeOffset>();
        
        // CRITICAL FIX: Lock bar time interpretation after first detection
        private enum BarTimeInterpretation { UTC, Chicago }
        private BarTimeInterpretation? _barTimeInterpretation = null;
        private bool _barTimeInterpretationLocked = false;
        
        // CRITICAL PERFORMANCE FIX: Rate-limit BAR_TIME_INTERPRETATION_MISMATCH warnings
        // After disconnect, NinjaTrader sends many out-of-order historical bars, causing thousands of warnings
        // Rate-limit to once per minute per instrument to prevent log flooding and NinjaTrader slowdown
        private readonly Dictionary<string, DateTimeOffset> _lastBarTimeMismatchLogUtc = new Dictionary<string, DateTimeOffset>();
        private const int BAR_TIME_MISMATCH_RATE_LIMIT_MINUTES = 1; // Log at most once per minute per instrument

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
                var engineInstrumentName = Instrument.MasterInstrument.Name;
                _engine = new RobotEngine(projectRoot, TimeSpan.FromSeconds(2), ExecutionMode.SIM, customLogDir: null, customTimetablePath: null, instrument: engineInstrumentName);
                
                // PHASE 1: Set account info for startup banner
                // Reuse accountName variable declared above
                var environment = _simAccountVerified ? "SIM" : "UNKNOWN";
                _engine.SetAccountInfo(accountName, environment);
                
                // Start engine
                _engine.Start();
                
                // Set session start time from TradingHours (if available)
                try
                {
                    var tradingHours = Instrument.MasterInstrument.TradingHours;
                    if (tradingHours != null && tradingHours.Sessions != null && tradingHours.Sessions.Count > 0)
                    {
                        // Get first session's begin time (in hhmm format, e.g., 1700 = 17:00)
                        var beginTime = tradingHours.Sessions[0].BeginTime;
                        if (beginTime > 0)
                        {
                            // Convert hhmm int to HH:MM string (e.g., 1700 -> "17:00")
                            var hours = beginTime / 100;
                            var minutes = beginTime % 100;
                            var sessionStartTime = $"{hours:D2}:{minutes:D2}";
                            
                            _engine.SetSessionStartTime(engineInstrumentName, sessionStartTime);
                            Log($"Session start time set from TradingHours: {sessionStartTime} for {engineInstrumentName}", LogLevel.Information);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log but don't fail - will fall back to default 17:00 CST
                    Log($"Warning: Could not extract session start from TradingHours: {ex.Message}. Using default 17:00 CST.", LogLevel.Warning);
                }

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

                // CRITICAL FIX: Gate BarsRequest on stream readiness (Option A - deterministic, no retries)
                // Pattern: Streams created THEN BarsRequest (single-shot, deterministic)
                // This ensures streams exist before requesting bars, preventing race conditions
                // CRITICAL: Wrap stream readiness check in try-catch to ensure _engineReady is set even if no streams exist
                // This allows the engine to start and emit heartbeats even if the timetable has no enabled streams for this instrument
                var instrumentNameUpper = engineInstrumentName.ToUpperInvariant();
                try
                {
                    if (!_engine.AreStreamsReadyForInstrument(instrumentNameUpper))
                    {
                        var warningMsg = $"WARNING: Cannot request historical bars - streams not ready for {instrumentNameUpper}. " +
                                       $"This may indicate: " +
                                       $"1) Timetable has no enabled streams for {instrumentNameUpper}, " +
                                       $"2) Streams were not created during engine.Start(), " +
                                       $"3) All streams for {instrumentNameUpper} are committed. " +
                                       $"Skipping BarsRequest - engine will continue without pre-hydration bars. " +
                                       $"Check logs for STREAMS_CREATED events.";
                        Log(warningMsg, LogLevel.Warning);
                        
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_SKIPPED", new Dictionary<string, object>
                        {
                            { "instrument", instrumentNameUpper },
                            { "reason", "Streams not ready" },
                            { "note", "Skipping BarsRequest - no enabled streams for this instrument. Engine will continue without pre-hydration bars." }
                        });
                        
                        // Don't throw - allow engine to continue without bars for this instrument
                        // This is expected if timetable doesn't have enabled streams for CL
                    }
                    else
                    {
                        // Streams are ready - request historical bars from NinjaTrader for pre-hydration (SIM mode)
                        // CRITICAL FIX: Wrap in try-catch to ensure _engineReady is set even if BarsRequest fails
                        // This ensures the timer can still call Tick() and emit heartbeats even if BarsRequest fails
                        try
                        {
                            RequestHistoricalBarsForPreHydration(instrumentNameUpper);
                        }
                        catch (Exception ex)
                        {
                            // Log error but don't prevent engine from being marked ready
                            // Engine can still process ticks and emit heartbeats even if BarsRequest fails
                            Log($"WARNING: BarsRequest failed but engine will continue: {ex.Message}", LogLevel.Warning);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Catch any unexpected errors during stream readiness check
                    // Log but don't prevent engine from being marked ready
                    Log($"WARNING: Stream readiness check failed but engine will continue: {ex.Message}", LogLevel.Warning);
                }

                // Get the adapter instance and wire NT context
                // Note: This requires exposing adapter from engine or using dependency injection
                // For now, we'll wire events directly to adapter via reflection or adapter registration
                WireNTContextToAdapter();
                
                // ENGINE_READY latch: Set once when all initialization is complete
                // This flag guards all execution paths to simplify reasoning and reduce repetition
                // CRITICAL: Set _engineReady even if BarsRequest failed, so timer can call Tick() and emit heartbeats
                _engineReady = true;
                Log("Engine ready - all initialization complete", LogLevel.Information);
                
                // CRITICAL FIX: Start timer in DataLoaded state for SIM mode
                // In SIM mode, the strategy may never reach Realtime state if market is closed,
                // but we still need heartbeats for watchdog monitoring
                // Start periodic timer for time-based state transitions (decoupled from bar arrivals)
                StartTickTimer();
                
                // Initialize heartbeat watchdog timestamp
                _lastSuccessfulTickUtc = DateTimeOffset.UtcNow;
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
                // Note: Timer may already be started from DataLoaded state, but StartTickTimer() is idempotent
                StartTickTimer();
                
                // Update heartbeat watchdog timestamp
                _lastSuccessfulTickUtc = DateTimeOffset.UtcNow;
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
        private void RequestHistoricalBarsForPreHydration(string instrumentName)
        {
            
            // FIX #3: Ensure every code path logs a final disposition event
            // Early return guard - log skipped
            if (_engine == null || Instrument == null)
            {
                if (_engine != null)
                {
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_SKIPPED", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "reason", "Engine or Instrument is null" },
                        { "engine_null", _engine == null },
                        { "instrument_null", Instrument == null },
                        { "note", "Cannot request bars - missing required context" }
                    });
                }
                return;
            }

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
                    
                    // Log failed before throwing
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_FAILED", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "trading_date", "NOT_LOCKED" },
                        { "range_start_time", "N/A" },
                        { "slot_time", "N/A" },
                        { "reason", "Trading date not locked" },
                        { "error", errorMsg },
                        { "note", "Trading date must be locked from timetable before requesting bars" }
                    });
                    
                    throw new InvalidOperationException(errorMsg);
                }

                // Parse trading date
                if (!DateOnly.TryParse(tradingDateStr, out var tradingDate))
                {
                    var errorMsg = $"Invalid trading date format: {tradingDateStr}";
                    Log(errorMsg, LogLevel.Warning);
                    
                    // Log skipped before returning
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_SKIPPED", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "trading_date", tradingDateStr },
                        { "range_start_time", "N/A" },
                        { "slot_time", "N/A" },
                        { "reason", "Invalid trading date format" },
                        { "error", errorMsg },
                        { "note", "Cannot parse trading date - skipping BarsRequest" }
                    });
                    return;
                }

                // CRITICAL: Get time range covering ALL enabled streams for this instrument
                // This ensures S1 (02:00) and S2 (08:00) streams both get their historical bars
                // The range covers from earliest range_start to latest slot_time across all enabled streams
                instrumentName = Instrument.MasterInstrument.Name.ToUpperInvariant();
                var timeRange = _engine.GetBarsRequestTimeRange(instrumentName);
                
                if (!timeRange.HasValue)
                {
                    var errorMsg = $"Cannot determine BarsRequest time range for {instrumentName}. " +
                                 $"This indicates no enabled streams exist for this instrument, or streams not yet created. " +
                                 $"Ensure timetable has enabled streams for {instrumentName} and engine.Start() completed successfully.";
                    Log(errorMsg, LogLevel.Error);
                    
                    // Log failed before throwing
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_FAILED", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "reason", "Cannot determine time range" },
                        { "error", errorMsg },
                        { "note", "No enabled streams found for this instrument" }
                    });
                    
                    throw new InvalidOperationException(errorMsg);
                }
                
                var (rangeStartChicago, slotTimeChicago) = timeRange.Value;
                
                if (string.IsNullOrWhiteSpace(rangeStartChicago) || string.IsNullOrWhiteSpace(slotTimeChicago))
                {
                    var errorMsg = $"Invalid BarsRequest time range for {instrumentName}: range_start={rangeStartChicago}, slot_time={slotTimeChicago}. " +
                                 $"Cannot proceed with BarsRequest.";
                    Log(errorMsg, LogLevel.Error);
                    
                    // Log failed before throwing
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_FAILED", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "trading_date", tradingDateStr },
                        { "reason", "Invalid time range" },
                        { "error", errorMsg },
                        { "range_start", rangeStartChicago ?? "NULL" },
                        { "slot_time", slotTimeChicago ?? "NULL" },
                        { "note", "Time range values are null or empty" }
                    });
                    
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
                // Note: NinjaTraderBarRequest returns QTSW2.Robot.Core.Bar (CoreBar), not NinjaTrader.Data.Bar
                // Use alias to avoid ambiguity with NinjaTrader.Data.Bar
                List<CoreBar> bars;
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
                    
                    // Log failed before throwing
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_FAILED", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "trading_date", tradingDateStr },
                        { "range_start_time", rangeStartChicago },
                        { "slot_time", slotTimeChicago },
                        { "end_time", endTimeChicago },
                        { "reason", "Exception during BarsRequest execution" },
                        { "error", ex.Message },
                        { "error_type", ex.GetType().Name },
                        { "stack_trace", ex.StackTrace },
                        { "note", "BarsRequest threw exception - check NinjaTrader historical data availability" }
                    });
                    
                    throw new InvalidOperationException(errorMsg, ex);
                }

                // Validate bars were returned
                if (bars == null)
                {
                    var errorMsg = $"CRITICAL: BarsRequest returned null for {tradingDateStr} ({rangeStartChicago} to {endTimeChicago}). " +
                                 $"This indicates a NinjaTrader API failure. Cannot proceed.";
                    Log(errorMsg, LogLevel.Error);
                    
                    // Log failed before throwing
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_FAILED", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "trading_date", tradingDateStr },
                        { "range_start_time", rangeStartChicago },
                        { "slot_time", slotTimeChicago },
                        { "end_time", endTimeChicago },
                        { "reason", "BarsRequest returned null" },
                        { "error", errorMsg },
                        { "note", "NinjaTrader API returned null - indicates API failure" }
                    });
                    
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
                    
                    // FIX #3: Log final disposition - EXECUTED with zero count
                    if (_engine != null)
                    {
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_EXECUTED", new Dictionary<string, object>
                        {
                            { "instrument", instrumentName },
                            { "trading_date", tradingDateStr },
                            { "bars_returned", 0 },
                            { "first_bar_utc", (string?)null },
                            { "last_bar_utc", (string?)null },
                            { "range_start_time", rangeStartChicago },
                            { "slot_time", slotTimeChicago },
                            { "end_time", endTimeChicago },
                            { "current_time_chicago", nowChicago.ToString("HH:mm") },
                            { "note", "BarsRequest executed successfully but returned zero bars - will rely on live bars" },
                            { "possible_causes", new[] { 
                                "Strategy started after slot_time (bars already passed)",
                                "NinjaTrader 'Days to load' setting too low",
                                "No historical data available for this date"
                            }}
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
                    
                    // FIX #3: Log final disposition - EXECUTED with bar count and timestamps
                    if (_engine != null)
                    {
                        var firstBarUtc = bars[0].TimestampUtc.ToString("o");
                        var lastBarUtc = bars[bars.Count - 1].TimestampUtc.ToString("o");
                        
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_EXECUTED", new Dictionary<string, object>
                        {
                            { "instrument", instrumentName },
                            { "trading_date", tradingDateStr },
                            { "bars_returned", bars.Count },
                            { "first_bar_utc", firstBarUtc },
                            { "last_bar_utc", lastBarUtc },
                            { "range_start_time", rangeStartChicago },
                            { "slot_time", slotTimeChicago },
                            { "end_time", endTimeChicago },
                            { "current_time_chicago", nowChicago.ToString("HH:mm") },
                            { "note", "BarsRequest executed successfully" }
                        });
                        
                        // Also log if count is unexpectedly low (less than 50% of expected)
                        // Calculate expected bar count (rough estimate: 1 bar per minute)
                        var rangeStartTime = timeService.ConstructChicagoTime(tradingDate, rangeStartChicago);
                        var endTime = (nowChicagoDate == tradingDate && nowChicago < slotTimeChicagoTime)
                            ? nowChicago
                            : timeService.ConstructChicagoTime(tradingDate, endTimeChicago);
                        var expectedMinutes = (int)(endTime - rangeStartTime).TotalMinutes;
                        var expectedBarCount = Math.Max(0, expectedMinutes);
                        
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
                var errorMsg = $"Failed to request historical bars from NinjaTrader: {ex.Message}. Will use file-based or live bars.";
                Log(errorMsg, LogLevel.Warning);
                
                // FIX #3: Log final disposition - FAILED
                if (_engine != null)
                {
                    try
                    {
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_FAILED", new Dictionary<string, object>
                        {
                            { "instrument", instrumentName },
                            { "reason", "Exception in outer catch handler" },
                            { "error", ex.Message },
                            { "error_type", ex.GetType().Name },
                            { "stack_trace", ex.StackTrace },
                            { "note", "BarsRequest failed - will fallback to file-based or live bars" }
                        });
                    }
                    catch
                    {
                        // Ignore logging errors in catch handler
                    }
                }
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
            
            // DIAGNOSTIC: Log when detection path is entered (first bar only)
            if (!_barTimeInterpretationLocked && _engine != null)
            {
                var instrumentName = Instrument.MasterInstrument.Name;
                _engine.LogEngineEvent(nowUtc, "BAR_TIME_DETECTION_STARTING", new Dictionary<string, object>
                {
                    { "instrument", instrumentName },
                    { "current_bar", CurrentBar },
                    { "bar_exchange_time", barExchangeTime.ToString("o") },
                    { "note", "Starting timezone detection for first bar" }
                });
            }
            
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
                if (_engine != null)
                {
                    var instrumentName = Instrument.MasterInstrument.Name;
                    var finalBarAge = (nowUtc - barUtc).TotalMinutes;
                    
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
                        { "invariant", $"Bar time interpretation LOCKED = {selectedInterpretation}. First bar age = {Math.Round(finalBarAge, 2)} minutes. Reason = {selectionReason}" }
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
                    // CRITICAL PERFORMANCE FIX: Rate-limit this warning to prevent log flooding
                    // After disconnect, NinjaTrader sends many out-of-order historical bars
                    // Logging every one causes thousands of warnings and slows down NinjaTrader
                    var instrumentName = Instrument.MasterInstrument.Name;
                    var shouldLog = !_lastBarTimeMismatchLogUtc.TryGetValue(instrumentName, out var lastLogUtc) ||
                                   (nowUtc - lastLogUtc).TotalMinutes >= BAR_TIME_MISMATCH_RATE_LIMIT_MINUTES;
                    
                    if (shouldLog && _engine != null)
                    {
                        _lastBarTimeMismatchLogUtc[instrumentName] = nowUtc;
                        _engine.LogEngineEvent(nowUtc, "BAR_TIME_INTERPRETATION_MISMATCH", new Dictionary<string, object>
                        {
                            { "instrument", instrumentName },
                            { "severity", "CRITICAL" },
                            { "locked_interpretation", _barTimeInterpretation.ToString() },
                            { "current_bar_age_minutes", Math.Round(barAge, 2) },
                            { "raw_bar_time", barExchangeTime.ToString("o") },
                            { "warning", "Bar time interpretation would flip - this should not happen" },
                            { "rate_limited", true },
                            { "note", $"Warning rate-limited to once per {BAR_TIME_MISMATCH_RATE_LIMIT_MINUTES} minute(s) per instrument to prevent log flooding. This typically occurs after disconnect when historical bars arrive out of order." }
                        });
                    }
                }
            }

            var open = (decimal)Open[0];
            var high = (decimal)High[0];
            var low = (decimal)Low[0];
            var close = (decimal)Close[0];

            // 🔒 INVARIANT GUARD: This conversion assumes fixed 1-minute bars
            // If BarsPeriod changes, AddMinutes(-1) becomes incorrect
            if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
            {
                var errorMsg = $"CRITICAL: Bar timestamp conversion requires 1-minute bars. " +
                              $"Current BarsPeriod: {BarsPeriod.BarsPeriodType}, Value: {BarsPeriod.Value}. " +
                              $"Cannot convert close time to open time. Stand down.";
                Log(errorMsg, LogLevel.Error);
                throw new InvalidOperationException(errorMsg);
            }

            // Convert bar timestamp from close time to open time (Analyzer parity)
            // INVARIANT: BarsPeriod == 1 minute (enforced above)
            var barUtcOpenTime = barUtc.AddMinutes(-1);

            // Deliver bar data to engine (bars only provide market data, not time advancement)
            _engine.OnBar(barUtcOpenTime, Instrument.MasterInstrument.Name, open, high, low, close, nowUtc);
            // NOTE: Tick() is now called by timer, not by bar arrivals
            // This ensures time-based state transitions occur even when no bars arrive
            
            // HEARTBEAT WATCHDOG: Check if timer is still calling Tick()
            // If no Tick() in HEARTBEAT_WATCHDOG_THRESHOLD_SECONDS, timer may have stopped
            if (_engineReady && _lastSuccessfulTickUtc != DateTimeOffset.MinValue)
            {
                var elapsedSinceLastTick = (nowUtc - _lastSuccessfulTickUtc).TotalSeconds;
                if (elapsedSinceLastTick > HEARTBEAT_WATCHDOG_THRESHOLD_SECONDS)
                {
                    Log($"WARNING: Tick timer appears to have stopped. Last successful Tick() was {elapsedSinceLastTick:.1f} seconds ago. Attempting to restart timer.", LogLevel.Warning);
                    // Attempt to restart timer
                    lock (_timerLock)
                    {
                        if (_tickTimer != null)
                        {
                            _tickTimer.Dispose();
                            _tickTimer = null;
                        }
                        StartTickTimer();
                        _lastSuccessfulTickUtc = nowUtc; // Reset watchdog timestamp
                    }
                }
            }
        }

        /// <summary>
        /// NT OrderUpdate event handler - forwards to adapter.
        /// </summary>
        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            if (_engine is null) return;
            
            // Update broker sync gate timestamp (before forwarding to adapter)
            var utcNow = DateTimeOffset.UtcNow;
            _engine.OnBrokerOrderUpdateObserved(utcNow);
            
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
            if (_engine is null) return;
            
            // Update broker sync gate timestamp (before forwarding to adapter)
            var utcNow = DateTimeOffset.UtcNow;
            _engine.OnBrokerExecutionUpdateObserved(utcNow);
            
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
                
                // CRITICAL: Call Tick() unconditionally - heartbeat emission is handled inside Tick()
                // Tick() will emit heartbeats even if engine is not trading-ready
                _engine.Tick(utcNow);
                
                // Update heartbeat watchdog timestamp on successful Tick() call
                _lastSuccessfulTickUtc = utcNow;
            }
            catch (Exception ex)
            {
                // Log but never throw - timer callbacks must not throw exceptions
                // CRITICAL: Log full exception details to diagnose why Tick() might be failing
                Log($"ERROR in tick timer callback: {ex.Message}\nStack trace: {ex.StackTrace}", LogLevel.Error);
                
                // CRITICAL: If timer callback fails repeatedly, NinjaTrader may stop calling it
                // Try to restart the timer as a defensive measure
                try
                {
                    lock (_timerLock)
                    {
                        if (_tickTimer != null)
                        {
                            // Timer still exists but callback is failing - log warning
                            Log("WARNING: Timer callback failed but timer still exists. Timer may stop calling callback.", LogLevel.Warning);
                        }
                    }
                }
                catch
                {
                    // Ignore errors in defensive restart attempt
                }
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
        
        /// <summary>
        /// Send a test notification to verify Pushover is working.
        /// Call this method from NinjaTrader or external code to test notifications.
        /// </summary>
        public void SendTestNotification()
        {
            if (_engine is null)
            {
                Log("ERROR: Cannot send test notification - engine is null", LogLevel.Error);
                return;
            }
            
            if (!_engineReady)
            {
                Log("WARNING: Cannot send test notification - engine not ready yet", LogLevel.Warning);
                return;
            }
            
            _engine.SendTestNotification();
            Log("Test notification requested - check logs for PUSHOVER_NOTIFY_ENQUEUED event", LogLevel.Information);
        }
    }
}
