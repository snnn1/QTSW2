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
        private bool _initFailed = false; // HARDENING FIX 3: Fail-closed flag - if true, strategy will not function
        
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
        
        // Diagnostic Point #1: Rate-limited OnBarUpdate logging
        private readonly Dictionary<string, DateTimeOffset> _lastOnBarUpdateLogUtc = new Dictionary<string, DateTimeOffset>();
        private const int ON_BAR_UPDATE_RATE_LIMIT_MINUTES = 1; // Log at most once per minute per instrument
        
        // Rate-limiting for BarsRequest skipped warnings (per instrument)
        private readonly Dictionary<string, DateTimeOffset> _lastBarsRequestSkippedLogUtc = new Dictionary<string, DateTimeOffset>();
        private const int BARSREQUEST_SKIPPED_RATE_LIMIT_MINUTES = 5; // Log at most once per 5 minutes per instrument

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "RobotSimStrategy";
                // CRITICAL FIX: Use OnBarClose to prevent NinjaTrader from blocking Realtime transition
                // OnEachTick requires tick data to be available, which may block MGC/MYM/M2K strategies
                // OnMarketData() override still provides tick-based BE detection when ticks are available
                Calculate = Calculate.OnBarClose; // Bar-based to avoid blocking Realtime transition, OnMarketData() handles tick-based BE
                IsUnmanaged = true; // Required for manual order management
                IsInstantiatedOnEachOptimizationIteration = false; // SIM mode only
                
                // Diagnostic: Check if NINJATRADER is defined
#if NINJATRADER
                // NINJATRADER is defined - real NT API will be used
#else
                // WARNING: NINJATRADER is NOT defined - mock implementation will be used
                // Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file
#endif
            }
            else if (State == State.DataLoaded)
            {
                try
                {
                    // Verify SIM account only
                    if (Account is null)
                    {
                        Log($"ERROR: Account is null. Aborting.", LogLevel.Error);
                        return;
                    }

                    // CRITICAL FIX: Verify Instrument exists before accessing properties
                    if (Instrument?.MasterInstrument == null)
                    {
                        Log($"ERROR: Instrument or MasterInstrument is null. Cannot initialize strategy. Aborting.", LogLevel.Error);
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

                    // Diagnostic: Log NINJATRADER compilation status
#if NINJATRADER
                    Log("NINJATRADER preprocessor directive is DEFINED - real NT API will be used", LogLevel.Information);
#else
                    Log("WARNING: NINJATRADER preprocessor directive is NOT DEFINED - mock implementation will be used. " +
                        "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild.", LogLevel.Warning);
#endif

                    // Initialize RobotEngine in SIM mode
                    // HARDENING FIX 1: Use strategy's Instrument as source of truth - do NOT call Instrument.GetInstrument() in DataLoaded
                    // This prevents blocking/hanging if instrument doesn't exist (e.g., M2K)
                    var projectRoot = ProjectRootResolver.ResolveProjectRoot();
                    
                    // CRITICAL FIX: Extract execution instrument name correctly for micro futures
                    // Instrument.FullName contains contract month (e.g., "MGC 03-26"), extract root (e.g., "MGC")
                    // Instrument.MasterInstrument.Name returns base instrument (e.g., "GC") which is wrong for micro futures
                    // For micro futures: MGC -> GC (base), M2K -> RTY (base), MES -> ES (base), etc.
                    var engineInstrumentName = Instrument.MasterInstrument.Name; // Default fallback
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(Instrument.FullName))
                        {
                            // Extract instrument root from FullName (e.g., "MGC 03-26" -> "MGC", "M2K 03-26" -> "M2K")
                            var fullNameParts = Instrument.FullName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (fullNameParts.Length > 0)
                            {
                                var extractedName = fullNameParts[0].ToUpperInvariant();
                                var masterName = Instrument.MasterInstrument.Name.ToUpperInvariant();
                                
                                // If extracted name differs from MasterInstrument.Name, use extracted name
                                // This handles: MGC (extracted) vs GC (master), M2K (extracted) vs RTY (master)
                                // For regular futures: ES (extracted) == ES (master), so use master is fine
                                if (extractedName != masterName)
                                {
                                    engineInstrumentName = extractedName;
                                }
                                else
                                {
                                    // Extracted name matches master - use master (regular futures)
                                    engineInstrumentName = Instrument.MasterInstrument.Name;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Fallback to MasterInstrument.Name if extraction fails
                        engineInstrumentName = Instrument.MasterInstrument.Name;
                    }
                    
                    // Use strategy's instrument directly - no resolution needed at this stage
                    // Instrument resolution will happen later when orders are submitted (in Realtime state)
                    _engine = new RobotEngine(projectRoot, TimeSpan.FromSeconds(2), ExecutionMode.SIM, customLogDir: null, customTimetablePath: null, instrument: engineInstrumentName);
                
                // PHASE 1: Set account info for startup banner
                // Reuse accountName variable declared above
                var environment = _simAccountVerified ? "SIM" : "UNKNOWN";
                _engine.SetAccountInfo(accountName, environment);
                
                // Start engine
                _engine.Start();
                
                // Set session start time from TradingHours (if available)
                // CRITICAL FIX: Wrap in try-catch and make non-blocking to prevent NinjaTrader state transition delays
                // TradingHours access can sometimes block if data isn't fully loaded
                try
                {
                    // Use null-conditional operators to safely access TradingHours
                    var tradingHours = Instrument?.MasterInstrument?.TradingHours;
                    if (tradingHours != null && tradingHours.Sessions != null && tradingHours.Sessions.Count > 0)
                    {
                        try
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
                        catch (Exception sessionEx)
                        {
                            // TradingHours.Sessions access can fail if data not loaded - don't block
                            Log($"Warning: Could not access TradingHours.Sessions: {sessionEx.Message}. Using default 17:00 CST.", LogLevel.Warning);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log but don't fail - will fall back to default 17:00 CST
                    // CRITICAL: Don't let TradingHours access block NinjaTrader state transition
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
                    // CRITICAL FIX: Don't throw exception - log error and continue
                    // Throwing prevents strategy from transitioning to Realtime state
                    // Strategy will continue but may not function properly
                    Log("WARNING: Continuing despite trading date not locked - strategy may not function properly", LogLevel.Warning);
                }

                // CRITICAL FIX: Request bars for ALL execution instruments from enabled streams
                // This ensures micro futures (MYM, MCL) route to base instrument streams (YM, CL) via canonical mapping
                // Pattern: Get all execution instruments → Request bars for each → Bars route via IsSameInstrument()
                try
                {
                    var executionInstruments = _engine.GetAllExecutionInstrumentsForBarsRequest();
                    
                    if (executionInstruments.Count == 0)
                    {
                        var warningMsg = $"WARNING: No execution instruments found for BarsRequest. " +
                                       $"This may indicate: " +
                                       $"1) Timetable has no enabled streams, " +
                                       $"2) Streams were not created during engine.Start(), " +
                                       $"3) All streams are committed. " +
                                       $"Skipping BarsRequest - engine will continue without pre-hydration bars. " +
                                       $"Check logs for STREAMS_CREATED events.";
                        Log(warningMsg, LogLevel.Warning);
                        
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_SKIPPED", new Dictionary<string, object>
                        {
                            { "reason", "No execution instruments found" },
                            { "note", "Skipping BarsRequest - no enabled streams. Engine will continue without pre-hydration bars." }
                        });
                    }
                    else
                    {
                        Log($"Requesting historical bars for {executionInstruments.Count} execution instrument(s): {string.Join(", ", executionInstruments)}", LogLevel.Information);
                        
                        // HARDENING FIX 2: Make BarsRequest fire-and-forget to prevent blocking DataLoaded
                        // Start BarsRequest in background thread pool - don't wait for completion
                        // This ensures strategy reaches Realtime state immediately, even if BarsRequest takes time
                        foreach (var executionInstrument in executionInstruments)
                        {
                            try
                            {
                                // Check if streams are ready for this instrument (handles canonical mapping)
                                if (!_engine.AreStreamsReadyForInstrument(executionInstrument))
                                {
                                    var warningMsg = $"WARNING: Cannot request historical bars - streams not ready for {executionInstrument}. " +
                                                   $"This may indicate streams were committed or timetable configuration issue. " +
                                                   $"Skipping BarsRequest for this instrument.";
                                    Log(warningMsg, LogLevel.Warning);
                                    
                                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_SKIPPED", new Dictionary<string, object>
                                    {
                                        { "instrument", executionInstrument },
                                        { "reason", "Streams not ready" },
                                        { "note", "Skipping BarsRequest - no enabled streams for this instrument." }
                                    });
                                    continue;
                                }
                                
                                // Fire-and-forget BarsRequest - don't block DataLoaded initialization
                                // Capture executionInstrument in local variable for closure
                                var instrument = executionInstrument;
                                ThreadPool.QueueUserWorkItem(_ =>
                                {
                                    try
                                    {
                                        RequestHistoricalBarsForPreHydration(instrument);
                                    }
                                    catch (Exception ex)
                                    {
                                        // Log error but don't affect strategy initialization
                                        Log($"WARNING: Background BarsRequest failed for {instrument}: {ex.Message}", LogLevel.Warning);
                                        
                                        if (_engine != null)
                                        {
                                            _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_BACKGROUND_FAILED", new Dictionary<string, object>
                                            {
                                                { "instrument", instrument },
                                                { "error", ex.Message },
                                                { "error_type", ex.GetType().Name },
                                                { "note", "BarsRequest failed in background thread - strategy continues with live bars only" }
                                            });
                                        }
                                    }
                                });
                                
                                Log($"BarsRequest queued for background execution: {executionInstrument}", LogLevel.Information);
                            }
                            catch (Exception ex)
                            {
                                // Log error but continue with other instruments
                                // Engine can still process bars and emit heartbeats even if BarsRequest fails for one instrument
                                Log($"WARNING: Failed to queue BarsRequest for {executionInstrument}: {ex.Message}", LogLevel.Warning);
                            }
                        }
                        
                        Log($"BarsRequest queued for {executionInstruments.Count} instrument(s) - continuing initialization without waiting", LogLevel.Information);
                    }
                }
                catch (Exception ex)
                {
                    // Catch any unexpected errors during BarsRequest loop
                    // Log but don't prevent engine from being marked ready
                    Log($"WARNING: BarsRequest loop failed but engine will continue: {ex.Message}", LogLevel.Warning);
                }

                // Get the adapter instance and wire NT context
                // Note: This requires exposing adapter from engine or using dependency injection
                // For now, we'll wire events directly to adapter via reflection or adapter registration
                try
                {
                    WireNTContextToAdapter();
                }
                catch (Exception ex)
                {
                    // CRITICAL FIX: Catch exceptions during adapter wiring to prevent strategy from hanging
                    Log($"CRITICAL: Failed to wire NT context to adapter: {ex.Message}. Strategy will not function properly.", LogLevel.Error);
                    Log($"Exception details: {ex.GetType().Name} - {ex.StackTrace}", LogLevel.Error);
                    // Don't set _engineReady = true, preventing further execution
                    return;
                }
                
                // ENGINE_READY latch: Set once when all initialization is complete
                // This flag guards all execution paths to simplify reasoning and reduce repetition
                _engineReady = true;
                var instrumentName = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
                Log($"Engine ready - all initialization complete. Instrument={instrumentName}, EngineReady={_engineReady}, InitFailed={_initFailed}", LogLevel.Information);
                
                // DIAGNOSTIC: Log initialization completion to help diagnose "stuck in loading" issues
                if (_engine != null)
                {
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "DATALOADED_INITIALIZATION_COMPLETE", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "engine_ready", _engineReady },
                        { "init_failed", _initFailed },
                        { "note", "DataLoaded initialization complete - strategy ready to transition to Realtime state" }
                    });
                }
                }
                catch (Exception ex)
                {
                    // HARDENING FIX 3: Fail closed on init exceptions - don't continue half-built
                    _initFailed = true;
                    Log($"CRITICAL: Exception during DataLoaded initialization: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                    Log($"Stack trace: {ex.StackTrace}", LogLevel.Error);
                    Log("Strategy initialization FAILED - strategy marked as INIT_FAILED and will not function. Check logs for details.", LogLevel.Error);
                    // Don't set _engineReady = true, preventing further execution
                    // Strategy will remain in DataLoaded state but won't crash NinjaTrader
                }
            }
            else if (State == State.Realtime)
            {
                // Engine is ready and strategy is in Realtime state
                // No timer needed - Tick() is now driven by bar flow
                
                // DIAGNOSTIC: Log Realtime state transition to help diagnose "stuck in loading" issues
                var instrumentName = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
                Log($"REALTIME_STATE_REACHED: Strategy transitioned to Realtime state. Instrument={instrumentName}, EngineReady={_engineReady}, InitFailed={_initFailed}", LogLevel.Information);
                
                if (_engine != null)
                {
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "REALTIME_STATE_REACHED", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "engine_ready", _engineReady },
                        { "init_failed", _initFailed },
                        { "note", "Strategy successfully transitioned from DataLoaded to Realtime state" }
                    });
                }
            }
            else if (State == State.Terminated)
            {
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
            
            // HARDENING FIX 2: BarsRequest is non-blocking - skip if instrument can't be resolved
            // Use strategy's Instrument directly - don't call Instrument.GetInstrument() here
            // If instrument doesn't exist, skip BarsRequest and continue with live bars only
            // This prevents hangs and allows strategy to reach Realtime state

            try
            {
                // HARDENING FIX 2: BarsRequest is non-blocking - skip if trading date not locked
                var tradingDateStr = _engine.GetTradingDate();
                if (string.IsNullOrEmpty(tradingDateStr))
                {
                    var warningMsg = "WARNING: Cannot request historical bars - trading date not yet locked from timetable. " +
                                   $"This indicates a configuration error: timetable missing or invalid trading_date. " +
                                   $"Skipping BarsRequest - will continue with live bars only.";
                    Log(warningMsg, LogLevel.Warning);
                    
                    // Log skipped but continue
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_SKIPPED", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "trading_date", "NOT_LOCKED" },
                        { "range_start_time", "N/A" },
                        { "slot_time", "N/A" },
                        { "reason", "Trading date not locked" },
                        { "note", "Skipping BarsRequest - continuing without pre-hydration bars (non-blocking)" }
                    });
                    
                    // Don't throw - allow strategy to continue
                    return;
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
                
                // HARDENING FIX 2: BarsRequest is non-blocking - skip if time range can't be determined
                if (!timeRange.HasValue)
                {
                    var warningMsg = $"WARNING: Cannot determine BarsRequest time range for {instrumentName}. " +
                                    $"This indicates no enabled streams exist for this instrument, or streams not yet created. " +
                                    $"Skipping BarsRequest - will continue with live bars only.";
                    Log(warningMsg, LogLevel.Warning);
                    
                    // Log skipped but continue
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_SKIPPED", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "reason", "Cannot determine time range" },
                        { "note", "No enabled streams found - skipping BarsRequest (non-blocking)" }
                    });
                    
                    // Don't throw - allow strategy to continue
                    return;
                }
                
                var (rangeStartChicago, slotTimeChicago) = timeRange.Value;
                
                // HARDENING FIX 2: BarsRequest is non-blocking - skip if time range invalid
                if (string.IsNullOrWhiteSpace(rangeStartChicago) || string.IsNullOrWhiteSpace(slotTimeChicago))
                {
                    // Rate-limit this message to prevent log spam
                    var skipLogTimeUtc = DateTimeOffset.UtcNow;
                    var shouldLog = !_lastBarsRequestSkippedLogUtc.TryGetValue(instrumentName, out var lastLogUtc) ||
                                   (skipLogTimeUtc - lastLogUtc).TotalMinutes >= BARSREQUEST_SKIPPED_RATE_LIMIT_MINUTES;
                    
                    if (shouldLog)
                    {
                        _lastBarsRequestSkippedLogUtc[instrumentName] = skipLogTimeUtc;
                        
                        var infoMsg = $"BarsRequest skipped for {instrumentName}: invalid time range (range_start={rangeStartChicago}, slot_time={slotTimeChicago}). " +
                                     $"Continuing with live bars only.";
                        Log(infoMsg, LogLevel.Information);
                        
                        // Log skipped but continue
                        _engine.LogEngineEvent(skipLogTimeUtc, "BARSREQUEST_SKIPPED", new Dictionary<string, object>
                        {
                            { "instrument", instrumentName },
                            { "trading_date", tradingDateStr },
                            { "reason", "Invalid time range" },
                            { "range_start", rangeStartChicago ?? "NULL" },
                            { "slot_time", slotTimeChicago ?? "NULL" },
                            { "note", "Time range values are null or empty - skipping BarsRequest (non-blocking)" }
                        });
                    }
                    
                    // Don't throw - allow strategy to continue
                    return;
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
                // CRITICAL: Use ConstructChicagoTime + ConvertChicagoToUtc instead of deprecated ConvertChicagoLocalToUtc
                var requestStartChicago = timeService.ConstructChicagoTime(tradingDate, rangeStartChicago);
                var requestStartUtc = timeService.ConvertChicagoToUtc(requestStartChicago);
                var requestEndChicago = timeService.ConstructChicagoTime(tradingDate, endTimeChicago);
                var requestEndUtc = timeService.ConvertChicagoToUtc(requestEndChicago);
                
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
                    // HARDENING FIX 2: BarsRequest is non-blocking - log error but don't throw
                    // Pre-hydration should never prevent strategy from reaching Realtime
                    // HARDENING FIX 2: BarsRequest timeout is expected and non-blocking - log as Info, not Warning
                    // The strategy will continue with live bars only, which is the intended fallback behavior
                    var errorMsg = $"BarsRequest timed out for {tradingDateStr} ({rangeStartChicago} to {endTimeChicago}). " +
                                 $"Error: {ex.Message}. " +
                                 $"Continuing without pre-hydration bars - strategy will rely on live bars only.";
                    Log(errorMsg, LogLevel.Information);
                    
                    // Log failed but continue
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
                        { "note", "BarsRequest failed - continuing without pre-hydration bars (non-blocking)" }
                    });
                    
                    // Don't throw - allow strategy to continue with live bars only
                    return;
                }

                // HARDENING FIX 2: BarsRequest is non-blocking - log warning but continue
                if (bars == null)
                {
                    var errorMsg = $"WARNING: BarsRequest returned null for {tradingDateStr} ({rangeStartChicago} to {endTimeChicago}). " +
                                 $"This indicates a NinjaTrader API failure. Will continue without pre-hydration bars.";
                    Log(errorMsg, LogLevel.Warning);
                    
                    // Log failed but continue
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_FAILED", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "trading_date", tradingDateStr },
                        { "range_start_time", rangeStartChicago },
                        { "slot_time", slotTimeChicago },
                        { "end_time", endTimeChicago },
                        { "reason", "BarsRequest returned null" },
                        { "error", errorMsg },
                        { "note", "NinjaTrader API returned null - continuing without pre-hydration bars (non-blocking)" }
                    });
                    
                    // Don't throw - allow strategy to continue with live bars only
                    return;
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
                    // CRITICAL: Use instrumentName (from BarsRequest) not Instrument.MasterInstrument.Name (strategy instrument)
                    // This ensures micro futures (MYM) route to base instrument streams (YM) via canonical mapping
                    // Note: Streams should exist by now (created in engine.Start())
                    _engine.LoadPreHydrationBars(instrumentName, bars, DateTimeOffset.UtcNow);
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
                // BarsRequest failures are expected and non-blocking - log as Info, not Warning
                var errorMsg = $"BarsRequest failed: {ex.Message}. Continuing with file-based or live bars.";
                Log(errorMsg, LogLevel.Information);
                
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
            
            var instrumentName = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
            Log($"NT context wired to adapter: Account={Account.Name}, Instrument={instrumentName}", LogLevel.Information);
            
            // DIAGNOSTIC: Log adapter wiring completion
            if (_engine != null)
            {
                _engine.LogEngineEvent(DateTimeOffset.UtcNow, "NT_CONTEXT_WIRED", new Dictionary<string, object>
                {
                    { "instrument", instrumentName },
                    { "account", Account?.Name ?? "NULL" },
                    { "note", "NinjaTrader context successfully wired to adapter" }
                });
            }
        }

        protected override void OnBarUpdate()
        {
            try
            {
                // 🔴 Diagnostic Point #0: Fire BEFORE any early returns to confirm OnBarUpdate() is being called
                // This will help us understand if NinjaTrader is calling OnBarUpdate() at all
                if (_engine != null)
            {
                var diagInstrument = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
                var diagTimeUtc = DateTimeOffset.UtcNow;
                var diagCanLog = !_lastOnBarUpdateLogUtc.TryGetValue(diagInstrument, out var diagPrevLogUtc) ||
                               (diagTimeUtc - diagPrevLogUtc).TotalMinutes >= ON_BAR_UPDATE_RATE_LIMIT_MINUTES;
                
                if (diagCanLog)
                {
                    _lastOnBarUpdateLogUtc[diagInstrument] = diagTimeUtc;
                    _engine.LogEngineEvent(diagTimeUtc, "ONBARUPDATE_CALLED", new Dictionary<string, object>
                    {
                        { "instrument", diagInstrument },
                        { "engine_ready", _engineReady },
                        { "engine_null", _engine is null },
                        { "current_bar", CurrentBar },
                        { "state", State.ToString() },
                        { "note", "Diagnostic Point #0: Confirms OnBarUpdate() is being called by NinjaTrader. If this doesn't fire, NinjaTrader isn't calling OnBarUpdate() at all." }
                    });
                }
            }
            
            // HARDENING FIX 3: Fail closed - don't execute if init failed
            if (_initFailed)
            {
                // Strategy initialization failed - do not process bars or execute trades
                return;
            }
            
            // Use ENGINE_READY latch to guard execution
            if (!_engineReady || _engine is null) return;
            if (CurrentBar < 1) return;
            
            // 🔴 Diagnostic Point #1: Rate-limited OnBarUpdate logging (ground truth)
            // NOTE: This diagnostic fires even if enable_diagnostic_logs is false, because it's the ground truth
            // If this doesn't fire, OnBarUpdate() isn't being called by NinjaTrader
            var diagInstrument1 = Instrument.MasterInstrument.Name;
            var diagTimeUtc1 = DateTimeOffset.UtcNow;
            var diagCanLog1 = !_lastOnBarUpdateLogUtc.TryGetValue(diagInstrument1, out var diagPrevLogUtc1) ||
                           (diagTimeUtc1 - diagPrevLogUtc1).TotalMinutes >= ON_BAR_UPDATE_RATE_LIMIT_MINUTES;
            
            if (diagCanLog1 && _engine != null)
            {
                _lastOnBarUpdateLogUtc[diagInstrument1] = diagTimeUtc1;
                _engine.LogEngineEvent(diagTimeUtc1, "ONBARUPDATE_DIAGNOSTIC", new Dictionary<string, object>
                {
                    { "instrument", diagInstrument1 },
                    { "bars_in_progress", BarsInProgress },
                    { "bar_time", Times[0][0].ToString("o") },
                    { "state", State.ToString() },
                    { "current_bar", CurrentBar },
                    { "engine_ready", _engineReady },
                    { "note", "Diagnostic Point #1: Ground truth - what NinjaTrader is feeding. If this doesn't fire, OnBarUpdate() isn't being called." }
                });
            }

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
                
                // Log to engine before throwing
                if (_engine != null)
                {
                    try
                    {
                        _engine.LogEngineEvent(nowUtc, "BARS_PERIOD_INVALID", new Dictionary<string, object>
                        {
                            { "bars_period_type", BarsPeriod.BarsPeriodType.ToString() },
                            { "bars_period_value", BarsPeriod.Value },
                            { "error", errorMsg },
                            { "note", "Invalid bars period - strategy cannot continue" }
                        });
                    }
                    catch
                    {
                        // If logging fails, still throw
                    }
                }
                
                // Don't throw - log error and return to prevent crash
                // Strategy will be disabled but won't crash NinjaTrader
                return;
            }

            // Convert bar timestamp from close time to open time (Analyzer parity)
            // INVARIANT: BarsPeriod == 1 minute (enforced above)
            var barUtcOpenTime = barUtc.AddMinutes(-1);

            // Deliver bar data to engine (bars only provide market data, not time advancement)
            _engine.OnBar(barUtcOpenTime, Instrument.MasterInstrument.Name, open, high, low, close, nowUtc);
            
            // PATTERN 1: Drive Tick() from bar flow (bar-driven liveness)
            // Tick() is now invoked only when bars arrive, not from a synthetic timer
            _engine.Tick(nowUtc);
            
            // Break-even monitoring: Now handled in OnMarketData() for tick-based detection
            // Removed CheckBreakEvenTriggers() call - BE checks happen on every tick, not bar close
            }
            catch (Exception ex)
            {
                // CRITICAL: Catch all exceptions to prevent NinjaTrader chart crashes
                // Log error but don't rethrow - allow strategy to continue
                var errorMsg = $"OnBarUpdate exception: {ex.GetType().Name}: {ex.Message}";
                Log(errorMsg, LogLevel.Error);
                
                // Log to engine if available
                if (_engine != null)
                {
                    try
                    {
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "ONBARUPDATE_EXCEPTION", new Dictionary<string, object>
                        {
                            { "exception_type", ex.GetType().Name },
                            { "exception_message", ex.Message },
                            { "stack_trace", ex.StackTrace ?? "N/A" },
                            { "instrument", Instrument?.MasterInstrument?.Name ?? "UNKNOWN" },
                            { "note", "Exception caught to prevent chart crash - strategy will continue" }
                        });
                    }
                    catch
                    {
                        // If logging fails, at least we caught the exception
                    }
                }
            }
        }
        
        /// <summary>
        /// OnMarketData override: Process tick data for break-even detection.
        /// Only processes Last price ticks (actual trades) to avoid bid/ask noise.
        /// </summary>
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            try
            {
                // HARDENING FIX 3: Fail closed - don't execute if init failed
                if (_initFailed) return;
                
                // Guard: Only process if engine ready
                if (!_engineReady || _adapter == null || _engine == null) return;
                
                // CRITICAL: Only process Last price ticks (avoid bid/ask noise)
                if (e.MarketDataType != MarketDataType.Last) return;
                
                // Guard: Only process ticks for this strategy's instrument
                if (e.Instrument != Instrument) return;
                
                // Get tick price
                var tickPrice = (decimal)e.Price;
                var utcNow = DateTimeOffset.UtcNow;
                
                // Check break-even triggers using tick price
                CheckBreakEvenTriggersTickBased(tickPrice, utcNow);
            }
            catch (Exception ex)
            {
                // CRITICAL: Catch all exceptions to prevent NinjaTrader chart crashes
                // Log error but don't rethrow - allow strategy to continue
                var errorMsg = $"OnMarketData exception: {ex.GetType().Name}: {ex.Message}";
                Log(errorMsg, LogLevel.Error);
                
                // Log to engine if available
                if (_engine != null)
                {
                    try
                    {
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "ONMARKETDATA_EXCEPTION", new Dictionary<string, object>
                        {
                            { "exception_type", ex.GetType().Name },
                            { "exception_message", ex.Message },
                            { "stack_trace", ex.StackTrace ?? "N/A" },
                            { "instrument", Instrument?.MasterInstrument?.Name ?? "UNKNOWN" },
                            { "market_data_type", e?.MarketDataType.ToString() ?? "N/A" },
                            { "note", "Exception caught to prevent chart crash - strategy will continue" }
                        });
                    }
                    catch
                    {
                        // If logging fails, at least we caught the exception
                    }
                }
            }
        }
        
        /// <summary>
        /// Check break-even triggers for all active intents and modify stop orders when triggered.
        /// Tick-based version: Uses actual tick price instead of bar high/low for immediate detection.
        /// </summary>
        private void CheckBreakEvenTriggersTickBased(decimal tickPrice, DateTimeOffset utcNow)
        {
            try
            {
                // HARDENING FIX 3: Fail closed - don't execute if init failed
                if (_initFailed) return;
                
                if (_adapter == null || !_engineReady) return;
                
                // Get active intents that need BE monitoring
                var activeIntents = _adapter.GetActiveIntentsForBEMonitoring();
                
                foreach (var (intentId, intent, beTriggerPrice, entryPrice, direction) in activeIntents)
                {
                // Check if BE trigger has been reached using tick price
                bool beTriggerReached = false;
                
                if (direction == "Long")
                {
                    // For long positions: trigger when tick price >= BE trigger price
                    beTriggerReached = tickPrice >= beTriggerPrice;
                }
                else if (direction == "Short")
                {
                    // For short positions: trigger when tick price <= BE trigger price
                    beTriggerReached = tickPrice <= beTriggerPrice;
                }
                
                if (beTriggerReached)
                {
                    // Calculate break-even stop price (entry ± 1 tick)
                    // CRITICAL FIX: Use strategy's instrument tick size directly - don't call Instrument.GetInstrument()
                    // Instrument.GetInstrument() can block/hang if instrument doesn't exist (e.g., M2K)
                    // Strategy's Instrument is already loaded and available - use it as source of truth
                    decimal tickSize = 0.25m; // Default fallback
                    try
                    {
                        // Use strategy's instrument tick size (already loaded, no resolution needed)
                        if (Instrument != null && Instrument.MasterInstrument != null)
                        {
                            tickSize = (decimal)Instrument.MasterInstrument.TickSize;
                        }
                    }
                    catch
                    {
                        // Use default fallback if tick size access fails
                    }
                    
                    decimal beStopPrice = direction == "Long" 
                        ? entryPrice - tickSize  // 1 tick below entry for long
                        : entryPrice + tickSize; // 1 tick above entry for short
                    
                    // Modify stop order to break-even
                    // CRITICAL FIX: Add retry awareness for race condition (stop order may not be in account.Orders yet)
                    var modifyResult = _adapter.ModifyStopToBreakEven(intentId, intent.Instrument ?? "", beStopPrice, utcNow);
                    
                    if (modifyResult.Success)
                    {
                        // Log successful BE trigger
                        if (_engine != null)
                        {
                            _engine.LogEngineEvent(utcNow, "BE_TRIGGER_REACHED", new Dictionary<string, object>
                            {
                                { "intent_id", intentId },
                                { "instrument", intent.Instrument ?? "" },
                                { "stream", intent.Stream ?? "" },
                                { "direction", direction },
                                { "entry_price", entryPrice },
                                { "be_trigger_price", beTriggerPrice },
                                { "be_stop_price", beStopPrice },
                                { "tick_size", tickSize },
                                { "tick_price", tickPrice },
                                { "detection_method", "TICK_BASED" },
                                { "note", "Break-even trigger reached (tick-based detection) - stop order modified to break-even" }
                            });
                        }
                    }
                    else
                    {
                        // Log failure with retry awareness
                        // CRITICAL FIX: Handle race condition where stop order may not be in account.Orders yet
                        // Use IndexOf for .NET Framework compatibility (Contains with StringComparison may not be available)
                        var errorMsg = modifyResult.ErrorMessage ?? "";
                        var isRetryableError = (errorMsg.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                              (errorMsg.IndexOf("Stop order", StringComparison.OrdinalIgnoreCase) >= 0);
                        
                        if (_engine != null)
                        {
                            _engine.LogEngineEvent(utcNow, isRetryableError ? "BE_TRIGGER_RETRY_NEEDED" : "BE_TRIGGER_FAILED", new Dictionary<string, object>
                            {
                                { "intent_id", intentId },
                                { "instrument", intent.Instrument ?? "" },
                                { "stream", intent.Stream ?? "" },
                                { "direction", direction },
                                { "be_trigger_price", beTriggerPrice },
                                { "be_stop_price", beStopPrice },
                                { "tick_size", tickSize },
                                { "tick_price", tickPrice },
                                { "detection_method", "TICK_BASED" },
                                { "error", modifyResult.ErrorMessage },
                                { "is_retryable", isRetryableError },
                                { "note", isRetryableError 
                                    ? "Break-even trigger reached but stop order not found yet (race condition) - will retry on next tick"
                                    : "Break-even trigger reached but stop modification failed" }
                            });
                        }
                    }
                }
                }
            }
            catch (Exception ex)
            {
                // CRITICAL: Catch all exceptions to prevent NinjaTrader chart crashes
                // Log error but don't rethrow - allow strategy to continue
                var errorMsg = $"CheckBreakEvenTriggersTickBased exception: {ex.GetType().Name}: {ex.Message}";
                Log(errorMsg, LogLevel.Error);
                
                // Log to engine if available
                if (_engine != null)
                {
                    try
                    {
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "CHECK_BE_TRIGGERS_EXCEPTION", new Dictionary<string, object>
                        {
                            { "exception_type", ex.GetType().Name },
                            { "exception_message", ex.Message },
                            { "stack_trace", ex.StackTrace ?? "N/A" },
                            { "tick_price", tickPrice },
                            { "instrument", Instrument?.MasterInstrument?.Name ?? "UNKNOWN" },
                            { "note", "Exception caught to prevent chart crash - strategy will continue" }
                        });
                    }
                    catch
                    {
                        // If logging fails, at least we caught the exception
                    }
                }
            }
        }

        /// <summary>
        /// NT OrderUpdate event handler - forwards to adapter.
        /// </summary>
        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            try
            {
                // HARDENING FIX 3: Fail closed - don't execute if init failed
                if (_initFailed) return;
                
                if (_engine is null) return;
                
                // Update broker sync gate timestamp (before forwarding to adapter)
                var utcNow = DateTimeOffset.UtcNow;
                _engine.OnBrokerOrderUpdateObserved(utcNow);
                
                if (_adapter is null) return;

                // Forward to adapter's HandleOrderUpdate method
                // Adapter will correlate order.Tag (intent_id) and update journal
                _adapter.HandleOrderUpdate(e.Order, e);
            }
            catch (Exception ex)
            {
                // CRITICAL: Catch all exceptions to prevent NinjaTrader chart crashes
                // The RuntimeBinderException we fixed earlier was likely causing crashes here
                var errorMsg = $"OnOrderUpdate exception: {ex.GetType().Name}: {ex.Message}";
                Log(errorMsg, LogLevel.Error);
                
                // Log to engine if available
                if (_engine != null)
                {
                    try
                    {
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "ONORDERUPDATE_EXCEPTION", new Dictionary<string, object>
                        {
                            { "exception_type", ex.GetType().Name },
                            { "exception_message", ex.Message },
                            { "stack_trace", ex.StackTrace ?? "N/A" },
                            { "order_id", e?.Order?.OrderId ?? "N/A" },
                            { "order_state", e?.Order?.OrderState.ToString() ?? "N/A" },
                            { "note", "Exception caught to prevent chart crash - strategy will continue" }
                        });
                    }
                    catch
                    {
                        // If logging fails, at least we caught the exception
                    }
                }
            }
        }

        /// <summary>
        /// NT ExecutionUpdate event handler - forwards to adapter.
        /// </summary>
        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
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
            catch (Exception ex)
            {
                // CRITICAL: Catch all exceptions to prevent NinjaTrader chart crashes
                var errorMsg = $"OnExecutionUpdate exception: {ex.GetType().Name}: {ex.Message}";
                Log(errorMsg, LogLevel.Error);
                
                // Log to engine if available
                if (_engine != null)
                {
                    try
                    {
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "ONEXECUTIONUPDATE_EXCEPTION", new Dictionary<string, object>
                        {
                            { "exception_type", ex.GetType().Name },
                            { "exception_message", ex.Message },
                            { "stack_trace", ex.StackTrace ?? "N/A" },
                            { "execution_price", e?.Execution?.Price ?? 0 },
                            { "execution_quantity", e?.Execution?.Quantity ?? 0 },
                            { "order_id", e?.Execution?.Order?.OrderId ?? "N/A" },
                            { "note", "Exception caught to prevent chart crash - strategy will continue" }
                        });
                    }
                    catch
                    {
                        // If logging fails, at least we caught the exception
                    }
                }
            }
        }


        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs connectionStatusUpdate)
        {
            try
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
            catch (Exception ex)
            {
                // CRITICAL: Catch all exceptions to prevent NinjaTrader chart crashes
                var errorMsg = $"OnConnectionStatusUpdate exception: {ex.GetType().Name}: {ex.Message}";
                Log(errorMsg, LogLevel.Error);
                
                // Log to engine if available
                if (_engine != null)
                {
                    try
                    {
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "ONCONNECTIONSTATUSUPDATE_EXCEPTION", new Dictionary<string, object>
                        {
                            { "exception_type", ex.GetType().Name },
                            { "exception_message", ex.Message },
                            { "note", "Exception caught to prevent chart crash - strategy will continue" }
                        });
                    }
                    catch
                    {
                        // If logging fails, at least we caught the exception
                    }
                }
            }
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
            
            // HARDENING FIX 3: Fail closed - don't execute if init failed
            if (_initFailed)
            {
                Log("ERROR: Cannot send test notification - strategy initialization failed", LogLevel.Error);
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
