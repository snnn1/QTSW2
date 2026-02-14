// NinjaTrader Strategy host for SIM execution
// This Strategy runs inside NinjaTrader and provides NT context (Account, Instrument, Events) to RobotEngine
//
// IMPORTANT: This Strategy MUST run in SIM account only.
// Copy into a NinjaTrader 8 strategy project and wire references to Robot.Core.
//
// REQUIRED FILES: When copying this file to NT project, also include:
// - NinjaTraderBarRequest.cs (from modules/robot/ninjatrader/)
// - Robot.Core.dll (or source files from modules/robot/core/)

// CRITICAL: Define NINJATRADER for NinjaTrader's compiler
// NinjaTrader compiles to tmp folder and may not respect .csproj DefineConstants
#define NINJATRADER

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// 
    /// INVARIANT: One execution instrument → one strategy instance per account.
    /// Multiple strategy instances on the same (account, executionInstrument) are invalid and will cause
    /// order tracking failures. This is enforced by duplicate instance detection during initialization.
    /// </summary>
    public class RobotSimStrategy : Strategy
    {
        // FUTURE HARDENING: Track active instances to prevent duplicate deployments
        // Key: (accountName, executionInstrumentFullName) - must be unique per account
        private static readonly HashSet<(string account, string instrument)> _activeInstances = new();
        private static readonly object _activeInstancesLock = new object();
        
        // Instance identifier for forensics (logged once at startup)
        private readonly string _instanceId;
        
        private RobotEngine? _engine;
        private NinjaTraderSimAdapter? _adapter;
        private bool _simAccountVerified = false;
        private bool _engineReady = false; // Single latch: true once engine is fully initialized and ready
        private bool _initFailed = false; // HARDENING FIX 3: Fail-closed flag - if true, strategy will not function
        
        // Track filtered callbacks for misconfiguration detection
        private int _filteredOrderUpdateCount = 0;
        private int _filteredExecutionUpdateCount = 0;
        
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
        private readonly Dictionary<string, DateTimeOffset> _lastTickTimeDiagnosticUtc = new Dictionary<string, DateTimeOffset>();
        private const int ON_BAR_UPDATE_RATE_LIMIT_MINUTES = 1; // Log at most once per minute per instrument
        private const int CALCULATING_PROGRESS_INTERVAL_BARS = 250; // Log every N bars during Historical to diagnose "stuck on Calculating"
        
        
        // Rate-limiting for BarsRequest skipped warnings (per instrument)
        private readonly Dictionary<string, DateTimeOffset> _lastBarsRequestSkippedLogUtc = new Dictionary<string, DateTimeOffset>();
        private const int BARSREQUEST_SKIPPED_RATE_LIMIT_MINUTES = 5; // Log at most once per 5 minutes per instrument
        
        // CRITICAL OBSERVABILITY FIX: Rate-limited BE evaluation heartbeat log
        // This proves BE logic runs on every tick and shows what it finds
        private readonly Dictionary<string, DateTimeOffset> _lastBeEvaluationLogUtc = new Dictionary<string, DateTimeOffset>();
        private const int BE_EVALUATION_RATE_LIMIT_SECONDS = 1; // Log at most once per second per instrument

        // MES/lag fix: cache GetActiveIntentsForBEMonitoring() result so we don't hit journal + lock on every Last tick.
        private List<(string intentId, Intent intent, decimal beTriggerPrice, decimal entryPrice, decimal? actualFillPrice, string direction)>? _cachedActiveIntentsForBE;
        private DateTimeOffset _cachedActiveIntentsForBEUtc = DateTimeOffset.MinValue;
        private const double BE_INTENTS_CACHE_MS = 200; // Refresh at most ~5/sec; 200ms stale is fine for BE trigger detection.
        
        // OPTIONAL ENHANCEMENT: Track when BE trigger is reached but modification hasn't completed
        private readonly Dictionary<string, DateTimeOffset> _beTriggerReachedPendingModification = new Dictionary<string, DateTimeOffset>();
        private const int BE_TRIGGER_TIMEOUT_SECONDS = 5; // ERROR if trigger reached but no modification after 5 seconds

        // Chart lag fix: throttle BE modify attempts. We were calling ModifyStopToBreakEven on every Last tick until success,
        // causing hundreds of account.Orders iterations + NT Change() per second. Cap to one attempt per 200ms per intent.
        private readonly Dictionary<string, DateTimeOffset> _lastBeModifyAttemptUtcByIntent = new Dictionary<string, DateTimeOffset>();
        private const double BE_MODIFY_ATTEMPT_INTERVAL_MS = 200;
        
        // Phase 3.3: Latch last price every tick; run BE scan on cadence only (reduces work per tick).
        private decimal _lastTickPriceForBE = 0;
        private DateTimeOffset _lastBeCheckUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastBip1WorkUtc = DateTimeOffset.MinValue;
        private const double BE_SCAN_THROTTLE_MS = 200; // Run intent scan at most ~5/sec; latch updated every tick.
        
        // Lightweight heartbeat: BIP 0 only, every N bars. Decoupled from trade state; no secondary series.
        private const int HEARTBEAT_BARS_INTERVAL = 1; // Every bar = ~1 min on 1-min chart; 30–60s is fine for liveness.
        
        // Per-bar profiling: log when sections exceed threshold (chart lag diagnosis)
        private const int BAR_PROFILE_THRESHOLD_MS = 5;
        private readonly Dictionary<string, DateTimeOffset> _lastBarProfileLogUtc = new Dictionary<string, DateTimeOffset>();
        private const int BAR_PROFILE_RATE_LIMIT_SECONDS = 30;

        /// <summary>Check if diagnostic logging during Historical is enabled. Uses reflection for backward compatibility with older Robot.Core.dll.</summary>
        private static bool IsDiagnosticLoggingDuringHistoricalEnabled(RobotEngine? engine)
        {
            if (engine == null) return false;
            try
            {
                var method = engine.GetType().GetMethod("IsDiagnosticLoggingDuringHistoricalEnabled", Type.EmptyTypes);
                if (method != null)
                    return (bool)(method.Invoke(engine, null) ?? false);
            }
            catch { /* older DLL: method doesn't exist */ }
            return false;
        }

        /// <summary>Check if diagnostic slow logs (BAR_PROFILE_SLOW, TICK_SLOW) are enabled. Uses reflection for backward compatibility.</summary>
        private static bool IsDiagnosticSlowLogsEnabled(RobotEngine? engine)
        {
            if (engine == null) return false;
            try
            {
                var method = engine.GetType().GetMethod("IsDiagnosticSlowLogsEnabled", Type.EmptyTypes);
                if (method != null)
                    return (bool)(method.Invoke(engine, null) ?? false);
            }
            catch { /* older DLL: method doesn't exist */ }
            return false;
        }

        /// <summary>File-based lifecycle trace for diagnosing "stuck in Calculating". Writes to logs/robot/strategy_lifecycle.log.</summary>
        private static void TraceLifecycle(string step, string? instrument = null, string? state = null)
        {
            TraceLifecycle(step, instrument, state, null);
        }

        private static void TraceLifecycle(string step, string? instrument, string? state, string? instanceId, string? extra = null)
        {
            try
            {
                var root = Environment.GetEnvironmentVariable("QTSW2_PROJECT_ROOT") ?? "C:\\Users\\jakej\\QTSW2";
                var path = Path.Combine(root, "logs", "robot", "strategy_lifecycle.log");
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var parts = new List<string> { $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z", step };
                if (!string.IsNullOrEmpty(instrument)) parts.Add($"instrument={instrument}");
                if (!string.IsNullOrEmpty(state)) parts.Add($"state={state}");
                if (!string.IsNullOrEmpty(instanceId)) parts.Add($"instance={instanceId}");
                if (!string.IsNullOrEmpty(extra)) parts.Add(extra);
                var line = string.Join(" | ", parts) + Environment.NewLine;
                File.AppendAllText(path, line);
            }
            catch { /* never throw from trace */ }
        }

        public RobotSimStrategy()
        {
            // Generate unique instance ID for forensics
            _instanceId = Guid.NewGuid().ToString("N").Substring(0, 8);
        }
        
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "RobotSimStrategy";
                // CRITICAL FIX: Use OnBarClose to prevent NinjaTrader from blocking Realtime transition
                // OnEachTick requires tick data to be available, which may block MGC/MYM/M2K strategies
                // Secondary 1-second series provides BE + heartbeat (throttled to 5s)
                Calculate = Calculate.OnBarClose; // Bar-based to avoid blocking Realtime transition
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
            else if (State == State.Configure)
            {
                TraceLifecycle("Configure_ENTER", Instrument?.MasterInstrument?.Name, "Configure", _instanceId);
                // DISABLED: Secondary 1-second series adds 60x bars during Historical (23,400/day vs 390/day) → chart lag
                // AddDataSeries(BarsPeriodType.Second, 1);
                TraceLifecycle("Configure_EXIT", Instrument?.MasterInstrument?.Name, "Configure", _instanceId);
            }
            else if (State == State.DataLoaded)
            {
                TraceLifecycle("DataLoaded_ENTER", Instrument?.MasterInstrument?.Name, "DataLoaded");
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
                    
                    // FUTURE HARDENING: Check for duplicate instance deployment
                    // INVARIANT: (account, executionInstrument) must be unique
                    // If violated → CRITICAL + stand down
                    var executionInstrumentFullName = Instrument?.FullName ?? "UNKNOWN";
                    // Note: accountName already declared above (line 128), reuse it
                    var instanceKey = (accountName, executionInstrumentFullName);
                    
                    lock (_activeInstancesLock)
                    {
                        if (_activeInstances.Contains(instanceKey))
                        {
                            // CRITICAL: Duplicate instance detected - fail closed
                            var errorMsg = $"CRITICAL: Duplicate strategy instance detected. " +
                                         $"Another instance is already running on account '{accountName}' " +
                                         $"with execution instrument '{executionInstrumentFullName}'. " +
                                         $"INVARIANT VIOLATION: One execution instrument → one strategy instance per account. " +
                                         $"Standing down to prevent order tracking failures.";
                            
                            Log(errorMsg, LogLevel.Error);
                            _initFailed = true;
                            
                            // Log to engine and report critical if engine exists
                            if (_engine != null)
                            {
                                try
                                {
                                    // Use engine's logging (not temporary logger)
                                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "DUPLICATE_INSTANCE_DETECTED", new Dictionary<string, object>
                                    {
                                        ["error"] = errorMsg,
                                        ["account"] = accountName,
                                        ["execution_instrument"] = executionInstrumentFullName,
                                        ["instance_id"] = _instanceId,
                                        ["action"] = "STAND_DOWN",
                                        ["invariant"] = "One execution instrument → one strategy instance per account"
                                    });
                                    
                                    // Critical event (HealthMonitor removed; tail logs for CRITICAL_ENGINE_EVENT to alert)
                                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "CRITICAL_ENGINE_EVENT", new Dictionary<string, object>
                                    {
                                        ["critical_event_type"] = "DUPLICATE_INSTANCE_DETECTED",
                                        ["account"] = accountName,
                                        ["execution_instrument"] = executionInstrumentFullName,
                                        ["instance_id"] = _instanceId,
                                        ["error"] = errorMsg,
                                        ["action"] = "STAND_DOWN"
                                    });
                                }
                                catch
                                {
                                    // If engine logging fails, at least we've logged to NT console
                                }
                            }
                            // If engine is null, console log is sufficient (early failure path)
                            
                            return; // Abort initialization
                        }
                        
                        // Register this instance
                        _activeInstances.Add(instanceKey);
                    }
                    
                    // FUTURE HARDENING: Log instance ID once at startup for forensics
                    Log($"Strategy instance initialized: InstanceId={_instanceId}, Account={accountName}, ExecutionInstrument={executionInstrumentFullName}", LogLevel.Information);

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
                    // CRITICAL: Pass both execution instrument (e.g., "MGC") and MasterInstrument.Name (e.g., "GC")
                    // for explicit canonical matching per authoritative rule
                    var masterInstrumentName = Instrument.MasterInstrument.Name; // e.g., "GC" for "MGC 03-26"
                    _engine = new RobotEngine(projectRoot, TimeSpan.FromSeconds(5), ExecutionMode.SIM, customLogDir: null, customTimetablePath: null, instrument: engineInstrumentName, masterInstrumentName: masterInstrumentName);
                
                // PHASE 1: Set account info for startup banner
                // Reuse accountName variable declared above
                var environment = _simAccountVerified ? "SIM" : "UNKNOWN";
                _engine.SetAccountInfo(accountName, environment);
                
                // Start engine (loads timetable and creates streams before returning)
                _engine.Start();
                TraceLifecycle("DataLoaded_ENGINE_START_DONE", engineInstrumentName, "DataLoaded");
                
                // Make timetable visibility explicit in NinjaTrader Control Center log
                var timetablePath = _engine.GetTimetablePath();
                var tradingDateFromEngine = _engine.GetTradingDate();
                var streamCount = _engine.GetStreamCount();
                if (!string.IsNullOrEmpty(timetablePath))
                    Log($"Timetable read: path={timetablePath}, trading_date={(string.IsNullOrEmpty(tradingDateFromEngine) ? "(not locked)" : tradingDateFromEngine)}, stream_count={streamCount}", LogLevel.Information);
                else
                    Log("Timetable path not set - engine may not have loaded timetable", LogLevel.Warning);
                TraceLifecycle("DataLoaded_AFTER_TIMETABLE_LOG", engineInstrumentName, "DataLoaded");
                
                // LAG FIX: Skip TradingHours access in DataLoaded - it can block UI thread causing "stuck in buffering".
                // Use default 17:00 CST; session start is also set from TradingHours in Realtime when wiring.
                _engine.SetSessionStartTime(engineInstrumentName, "17:00");
                TraceLifecycle("DataLoaded_AFTER_SESSION_START", engineInstrumentName, "DataLoaded");

                var tradingDateStr = _engine.GetTradingDate();

                // CRITICAL: Verify engine startup succeeded before requesting bars
                // Check that trading date is locked and streams are created
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
                TraceLifecycle("DataLoaded_BEFORE_BARSREQUEST", engineInstrumentName, "DataLoaded");

                // CRITICAL FIX: Request bars for ALL execution instruments from enabled streams
                // This ensures micro futures (MYM, MCL) route to base instrument streams (YM, CL) via canonical mapping
                // Pattern: Get all execution instruments → Mark ALL as pending FIRST → Then queue BarsRequest for each
                try
                {
                    var executionInstruments = _engine.GetAllExecutionInstrumentsForBarsRequest();
                    
                    // NQ2 FIX: Enhanced logging for BarsRequest initiation
                    Log($"BARSREQUEST_INIT: Attempting to request bars. Execution instruments count: {executionInstruments.Count}", LogLevel.Information);
                    
                    if (_engine != null)
                    {
                        _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_INIT", new Dictionary<string, object>
                        {
                            { "execution_instruments_count", executionInstruments.Count },
                            { "execution_instruments", executionInstruments },
                            { "note", "BarsRequest initiation - checking for enabled streams" }
                        });
                    }
                    
                    // CRITICAL: Mark ALL instruments as pending BEFORE queuing BarsRequest
                    // This ensures streams wait even if they process ticks before BarsRequest completes
                    // This prevents race condition where stream checks IsBarsRequestPending before it's marked
                    foreach (var instrument in executionInstruments)
                    {
                        try
                        {
                            // Mark as pending immediately (synchronously) before queuing BarsRequest
                            _engine.MarkBarsRequestPending(instrument, DateTimeOffset.UtcNow);
                            Log($"BarsRequest marked as pending for {instrument} (before queuing)", LogLevel.Information);
                        }
                        catch (Exception markEx)
                        {
                            Log($"WARNING: Failed to mark BarsRequest pending for {instrument}: {markEx.Message}", LogLevel.Warning);
                        }
                    }
                    
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
                        
                        // Critical event (HealthMonitor removed; tail logs for CRITICAL_ENGINE_EVENT to alert)
                        _engine?.LogEngineEvent(DateTimeOffset.UtcNow, "CRITICAL_ENGINE_EVENT", new Dictionary<string, object>
                        {
                            ["critical_event_type"] = "BARSREQUEST_SKIPPED_NO_INSTRUMENTS",
                            ["reason"] = "No execution instruments found",
                            ["note"] = "BarsRequest was skipped - streams may not have historical bars"
                        });
                    }
                    else
                    {
                        Log($"Requesting historical bars for {executionInstruments.Count} execution instrument(s): {string.Join(", ", executionInstruments)}", LogLevel.Information);
                        
                        // NOTE: BarsRequest already marked as pending above (before checking count)
                        // This ensures streams wait even if they process ticks before BarsRequest completes
                        
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
                                    
                                    // Critical event (HealthMonitor removed; tail logs for CRITICAL_ENGINE_EVENT to alert)
                                    _engine?.LogEngineEvent(DateTimeOffset.UtcNow, "CRITICAL_ENGINE_EVENT", new Dictionary<string, object>
                                    {
                                        ["critical_event_type"] = "BARSREQUEST_SKIPPED_STREAMS_NOT_READY",
                                        ["instrument"] = executionInstrument,
                                        ["reason"] = "Streams not ready",
                                        ["note"] = "BarsRequest skipped - streams may not receive historical bars"
                                    });
                                    
                                    continue;
                                }
                                
                                // Fire-and-forget BarsRequest - don't block DataLoaded initialization
                                // Capture executionInstrument in local variable for closure
                                var instrument = executionInstrument;
                                
                                // NOTE: BarsRequest already marked as pending above (before this loop)
                                // This ensures streams wait even if they initialize before BarsRequest is queued
                                
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
                                            
                                            // Mark as completed (failed) so range lock can proceed
                                            // This prevents indefinite blocking if BarsRequest fails
                                            _engine.MarkBarsRequestCompleted(instrument, DateTimeOffset.UtcNow);
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

                if (_engine != null)
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "DATALOADED_BARSREQUEST_DONE", new Dictionary<string, object> { { "instrument", Instrument?.MasterInstrument?.Name ?? "" }, { "note", "BarsRequest block finished - proceeding to WireNTContext" } });
                TraceLifecycle("DataLoaded_AFTER_BARSREQUEST", Instrument?.MasterInstrument?.Name, "DataLoaded");

                // Get the adapter instance and wire NT context
                // Note: This requires exposing adapter from engine or using dependency injection
                // For now, we'll wire events directly to adapter via reflection or adapter registration
                if (_engine != null)
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "DATALOADED_WIRE_STARTING", new Dictionary<string, object> { { "instrument", Instrument?.MasterInstrument?.Name ?? "" }, { "note", "About to call WireNTContextToAdapter()" } });
                TraceLifecycle("DataLoaded_BEFORE_WIRE", Instrument?.MasterInstrument?.Name, "DataLoaded");
                try
                {
                    WireNTContextToAdapter();
                }
                catch (Exception ex)
                {
                    // CRITICAL FIX: Catch exceptions during adapter wiring to prevent strategy from hanging
                    Log($"CRITICAL: Failed to wire NT context to adapter: {ex.Message}. Strategy will not function properly.", LogLevel.Error);
                    Log($"Exception details: {ex.GetType().Name} - {ex.StackTrace}", LogLevel.Error);
                    if (_engine != null)
                    {
                        try
                        {
                            _engine.LogEngineEvent(DateTimeOffset.UtcNow, "DATALOADED_WIRE_FAILED", new Dictionary<string, object>
                            {
                                { "instrument", Instrument?.MasterInstrument?.Name ?? "" },
                                { "exception_type", ex.GetType().Name },
                                { "exception_message", ex.Message },
                                { "stack_trace", ex.StackTrace ?? "" },
                                { "note", "WireNTContextToAdapter threw - strategy will not function" }
                            });
                        }
                        catch { /* ignore */ }
                    }
                    // Don't set _engineReady = true, preventing further execution
                    return;
                }
                if (_engine != null)
                    _engine.LogEngineEvent(DateTimeOffset.UtcNow, "DATALOADED_WIRE_DONE", new Dictionary<string, object> { { "instrument", Instrument?.MasterInstrument?.Name ?? "" }, { "note", "WireNTContextToAdapter completed" } });
                TraceLifecycle("DataLoaded_WIRE_DONE", Instrument?.MasterInstrument?.Name, "DataLoaded");
                
                // ENGINE_READY latch: Set once when all initialization is complete
                // This flag guards all execution paths to simplify reasoning and reduce repetition
                _engineReady = true;
                TraceLifecycle("DataLoaded_ENGINE_READY_TRUE", Instrument?.MasterInstrument?.Name, "DataLoaded");
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
                    if (_engine != null)
                    {
                        try
                        {
                            _engine.LogEngineEvent(DateTimeOffset.UtcNow, "DATALOADED_INIT_EXCEPTION", new Dictionary<string, object>
                            {
                                { "instrument", Instrument?.MasterInstrument?.Name ?? "UNKNOWN" },
                                { "exception_type", ex.GetType().Name },
                                { "exception_message", ex.Message },
                                { "stack_trace", (ex.StackTrace ?? "").Length > 2000 ? (ex.StackTrace ?? "").Substring(0, 2000) + "..." : (ex.StackTrace ?? "") },
                                { "note", "DataLoaded initialization threw - strategy marked INIT_FAILED" }
                            });
                        }
                        catch { /* ignore */ }
                    }
                    // Don't set _engineReady = true, preventing further execution
                    // Strategy will remain in DataLoaded state but won't crash NinjaTrader
                }
            }
            else if (State == State.Realtime)
            {
                // Realtime: No timer (any timer caused contention). Tick + BE from OnBarUpdate only.

                TraceLifecycle("REALTIME_ENTER", Instrument?.MasterInstrument?.Name, "Realtime");
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
                        { "bars_array_length", BarsArray?.Length ?? 0 },
                        { "has_secondary_series", BarsArray != null && BarsArray.Length >= 2 },
                        { "be_detection_path", BarsArray != null && BarsArray.Length >= 2 ? "1-second series (BIP 1)" : "fallback (primary bar)" },
                        { "note", "Strategy successfully transitioned from DataLoaded to Realtime state. has_secondary_series=true enables BE detection via 1-second bars." }
                    });
                    
                    // RESTART-AWARE: Check if any streams need BarsRequest for restart
                    // This handles the case where streams restart mid-session and need historical bars
                    try
                    {
                        var instrumentsNeedingBarsRequest = _engine.GetInstrumentsNeedingRestartBarsRequest();
                        if (instrumentsNeedingBarsRequest.Count > 0)
                        {
                            Log($"RESTART_BARSREQUEST: Detected {instrumentsNeedingBarsRequest.Count} instrument(s) needing BarsRequest for restart", LogLevel.Information);
                            
                            foreach (var instrument in instrumentsNeedingBarsRequest)
                            {
                                // Check if this strategy's instrument matches
                                var canonicalInstrument = Instrument?.MasterInstrument?.Name ?? "";
                                if (string.Equals(instrument, canonicalInstrument, StringComparison.OrdinalIgnoreCase))
                                {
                                    Log($"RESTART_BARSREQUEST: Triggering BarsRequest for {instrument} due to restart", LogLevel.Information);
                                    
                                    // Mark BarsRequest as pending BEFORE queuing (prevents premature range lock)
                                    if (_engine != null)
                                    {
                                        _engine.MarkBarsRequestPending(instrument, DateTimeOffset.UtcNow);
                                    }
                                    
                                    // Trigger BarsRequest in background thread (non-blocking)
                                    var instrumentForClosure = instrument;
                                    ThreadPool.QueueUserWorkItem(_ =>
                                    {
                                        try
                                        {
                                            RequestHistoricalBarsForPreHydration(instrumentForClosure);
                                        }
                                        catch (Exception ex)
                                        {
                                            Log($"WARNING: Restart BarsRequest failed for {instrumentForClosure}: {ex.Message}", LogLevel.Warning);
                                            
                                            if (_engine != null)
                                            {
                                                _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BARSREQUEST_RESTART_FAILED", new Dictionary<string, object>
                                                {
                                                    { "instrument", instrumentForClosure },
                                                    { "error", ex.Message },
                                                    { "error_type", ex.GetType().Name },
                                                    { "note", "Restart BarsRequest failed - strategy continues with live bars only" }
                                                });
                                                
                                                // Mark as completed (failed) so range lock can proceed
                                                // This prevents indefinite blocking if BarsRequest fails
                                                _engine.MarkBarsRequestCompleted(instrumentForClosure, DateTimeOffset.UtcNow);
                                            }
                                        }
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but don't fail - BarsRequest check is best-effort
                        Log($"WARNING: Failed to check for restart BarsRequest: {ex.Message}", LogLevel.Warning);
                    }
                }
            }
            else if (State == State.Historical)
            {
                TraceLifecycle("Historical_ENTER", Instrument?.MasterInstrument?.Name, "Historical", _instanceId, "note=Calculating phase - OnBarUpdate will fire for each historical bar");
            }
            else if (State == State.Terminated)
            {
                TraceLifecycle("Terminated_ENTER", Instrument?.MasterInstrument?.Name, "Terminated", _instanceId);
                _engine?.Stop();

                // FUTURE HARDENING: Unregister instance on termination
                if (Account != null && Instrument != null)
                {
                    var executionInstrumentFullName = Instrument.FullName ?? "UNKNOWN";
                    var accountName = Account.Name ?? "UNKNOWN";
                    var instanceKey = (accountName, executionInstrumentFullName);
                    
                    lock (_activeInstancesLock)
                    {
                        _activeInstances.Remove(instanceKey);
                    }
                    
                    // Log filtered callback counts if any occurred (useful signal for misconfiguration)
                    if (_filteredOrderUpdateCount > 0 || _filteredExecutionUpdateCount > 0)
                    {
                        Log($"Instance terminated: InstanceId={_instanceId}, FilteredOrderUpdates={_filteredOrderUpdateCount}, FilteredExecutionUpdates={_filteredExecutionUpdateCount}", LogLevel.Information);
                    }
                }
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
                
                // RESTART-AWARE: On restart (after slot_time), request bars up to now
                // Use the earlier of: slotTimeChicago or now (to avoid future bars)
                // CRITICAL: On restart after slot_time, request bars up to current time for visibility
                // If we're on a different date (e.g., requesting tomorrow's bars), always use slot_time
                var endTimeChicago = (nowChicagoDate == tradingDate && nowChicago < slotTimeChicagoTime)
                    ? nowChicago.ToString("HH:mm")  // Before slot_time: request up to now
                    : (nowChicagoDate == tradingDate && nowChicago >= slotTimeChicagoTime)
                        ? nowChicago.ToString("HH:mm")  // RESTART: After slot_time, request up to now
                        : slotTimeChicago;  // Future date: use slot_time
                
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
                    Log($"RESTART_POLICY: Restarting after slot time ({slotTimeChicago}) - BarsRequest requesting bars up to current time ({nowChicago:HH:mm}) for visibility", LogLevel.Information);
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
                List<CoreBar>? bars = null;
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
                finally
                {
                    // Clear pending on ANY exit (timeout, exception, partial failure) — use same key as when marked pending
                    _engine.MarkBarsRequestCompleted(instrumentName, tradingDateStr);
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
                // Dormant: init failed or engine not ready — skip all work immediately (Tier 2.1)
                if (_initFailed || !_engineReady || _engine is null) return;

                // Strict phase control: skip only when State.Historical AND config disabled. Do NOT use !isRealtime — Transition exists between Historical and Realtime.
                var skipHistoricalDiagEarly = State == State.Historical && _engine != null && !IsDiagnosticLoggingDuringHistoricalEnabled(_engine);
                // Diagnostic Point #0: primary series only (restrict to avoid BIP 1 spam) — gated when diagnostic_logging_during_historical=false
                if (BarsInProgress == 0 && _engine != null && !skipHistoricalDiagEarly)
                {
                    var executionInstrumentFullName = Instrument?.FullName ?? "UNKNOWN";
                    var diagInstrument = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
                    var diagTimeUtc = DateTimeOffset.UtcNow;
                    var diagCanLog = !_lastOnBarUpdateLogUtc.TryGetValue(executionInstrumentFullName, out var diagPrevLogUtc) ||
                                   (diagTimeUtc - diagPrevLogUtc).TotalMinutes >= ON_BAR_UPDATE_RATE_LIMIT_MINUTES;
                    if (diagCanLog)
                    {
                        _lastOnBarUpdateLogUtc[executionInstrumentFullName] = diagTimeUtc;
                        _engine.LogEngineEvent(diagTimeUtc, "ONBARUPDATE_CALLED", new Dictionary<string, object>
                        {
                            { "instrument", diagInstrument },
                            { "execution_instrument_full_name", executionInstrumentFullName },
                            { "engine_ready", _engineReady },
                            { "engine_null", _engine is null },
                            { "current_bar", CurrentBar },
                            { "state", State.ToString() },
                            { "note", "Diagnostic Point #0: Confirms OnBarUpdate() is being called by NinjaTrader." }
                        });
                    }
                }

            if (CurrentBars[BarsInProgress] < 1) return;

            // Secondary series (BIP 1): BE + heartbeat only. DISABLED when AddDataSeries(Second,1) is commented out.
            // LAG FIX: Only run when in a position — skip entirely when flat to avoid chart lag.
            // Throttle to 5s when in position — 1/sec across 7+ strategies was blocking UI thread.
            if (BarsInProgress == 1)
            {
                if (State != State.Realtime)
                    return;
                if (Position.MarketPosition == MarketPosition.Flat)
                    return; // No trade — skip BE + heartbeat entirely
                var now = DateTimeOffset.UtcNow;
                if ((now - _lastBip1WorkUtc).TotalSeconds < 5.0 && _lastBip1WorkUtc != DateTimeOffset.MinValue)
                    return;
                _lastBip1WorkUtc = now;
                RunBreakEvenCheck();
                return;
            }

            // Primary series (BIP 0) only below — Times[0][0], Open[0], _engine.Tick(), hydration, etc.
            // FALLBACK 1: When secondary series disabled, run BE from BIP 0 (uses primary bar close, throttled 5s)
            // FALLBACK 2: When secondary enabled BUT 1-second bars not firing (e.g. MCL/M2K data feed), run BE from BIP 0
            var inPositionRealtime = State == State.Realtime && Position.MarketPosition != MarketPosition.Flat;
            var bip1StaleSeconds = 10.0; // If no BIP 1 in 10s, 1-second series likely not updating
            var runBeFromBip0 = inPositionRealtime && (
                BarsArray.Length < 2 ||
                (_lastBip1WorkUtc != DateTimeOffset.MinValue && (DateTimeOffset.UtcNow - _lastBip1WorkUtc).TotalSeconds >= bip1StaleSeconds));
            if (runBeFromBip0)
            {
                var now = DateTimeOffset.UtcNow;
                if (_lastBip1WorkUtc == DateTimeOffset.MinValue || (now - _lastBip1WorkUtc).TotalSeconds >= 5.0)
                {
                    _lastBip1WorkUtc = now;
                    var instrumentNameFallback = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
                    var hasSecondary = BarsArray != null && BarsArray.Length >= 2;
                    if (_engine != null && (!_lastBePathActiveLogUtc.TryGetValue(instrumentNameFallback, out var lastPathLogF) || (now - lastPathLogF).TotalSeconds >= BE_PATH_ACTIVE_RATE_LIMIT_SECONDS))
                    {
                        _lastBePathActiveLogUtc[instrumentNameFallback] = now;
                        var activeCountF = _adapter != null ? _adapter.GetActiveIntentsForBEMonitoring().Count : -1;
                        _engine.LogEngineEvent(now, "BE_PATH_ACTIVE", new Dictionary<string, object>
                        {
                            { "instrument", instrumentNameFallback },
                            { "bars_in_progress", 0 },
                            { "has_secondary_series", hasSecondary },
                            { "in_position", true },
                            { "active_intent_count", activeCountF },
                            { "note", hasSecondary ? "BE check (fallback: BIP 1 stale, using primary bar). 1-second series may not be updating." : "BE check path active (fallback: primary bar close)." }
                        });
                    }
                    RunBreakEvenCheck();
                }
            }
            var barProfileSw = Stopwatch.StartNew();
            long barProfileLastMs = 0L;
            var skipHistoricalDiag = skipHistoricalDiagEarly;
            
            // Lightweight heartbeat: Realtime only, every N bars. Minimal payload; decoupled from trade state.
            if (State == State.Realtime && CurrentBar > 0 && CurrentBar % HEARTBEAT_BARS_INTERVAL == 0 && _engine != null)
                _engine.LogEngineEvent(DateTimeOffset.UtcNow, "ENGINE_ALIVE", new Dictionary<string, object> { { "note", "heartbeat" } });
            
            // Lifecycle trace: first bar only so we know we're processing Historical (gated when diagnostic_logging_during_historical=false)
            if (!skipHistoricalDiag && CurrentBar == 1)
                TraceLifecycle("OnBarUpdate_FIRST_BAR", Instrument?.MasterInstrument?.Name, State.ToString(), _instanceId);
            // Extensive diagnostic: progress every N bars during Historical (gated when diagnostic_logging_during_historical=false)
            var logTiming = !skipHistoricalDiag && State == State.Historical && CurrentBar > 0 && CurrentBar % CALCULATING_PROGRESS_INTERVAL_BARS == 0;
            if (logTiming) TraceLifecycle("OnBarUpdate_PROGRESS", Instrument?.MasterInstrument?.Name, "Historical", _instanceId, $"bar={CurrentBar}");
            if (logTiming) TraceLifecycle("OnBarUpdate_BEFORE_BARTIME", Instrument?.MasterInstrument?.Name, "Historical", _instanceId, $"bar={CurrentBar}");
            
            // Diagnostic Point #1: primary series only (BIP 0) — gated when diagnostic_logging_during_historical=false
            var diagInstrument1 = Instrument.MasterInstrument.Name;
            var diagTimeUtc1 = DateTimeOffset.UtcNow;
            var diagCanLog1 = !skipHistoricalDiag && (!_lastOnBarUpdateLogUtc.TryGetValue(diagInstrument1, out var diagPrevLogUtc1) ||
                           (diagTimeUtc1 - diagPrevLogUtc1).TotalMinutes >= ON_BAR_UPDATE_RATE_LIMIT_MINUTES);
            
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
            LogBarProfileIfSlow("diagnostics", barProfileSw.ElapsedMilliseconds - barProfileLastMs);
            barProfileLastMs = barProfileSw.ElapsedMilliseconds;

            // CRITICAL FIX: NinjaTrader Times[0][0] timezone handling with locking
            // Lock interpretation after first detection to prevent mid-run flips
            var barExchangeTime = Times[0][0]; // Exchange time from NinjaTrader
            var nowUtc = DateTimeOffset.UtcNow;
            DateTimeOffset barUtc;
            
            // DIAGNOSTIC: Log when detection path is entered (first bar only) — gated when diagnostic_logging_during_historical=false
            if (!skipHistoricalDiag && !_barTimeInterpretationLocked && _engine != null)
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
                
                // CRITICAL INVARIANT LOG: Explicit log right after locking (gated when diagnostic_logging_during_historical=false)
                // This ensures if interpretation is wrong, we know immediately in logs (seconds, not hours)
                if (!skipHistoricalDiag && _engine != null)
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
            LogBarProfileIfSlow("bar_time", barProfileSw.ElapsedMilliseconds - barProfileLastMs);
            barProfileLastMs = barProfileSw.ElapsedMilliseconds;

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
            if (logTiming) TraceLifecycle("OnBarUpdate_BEFORE_ONBAR", Instrument?.MasterInstrument?.Name, "Historical", _instanceId, $"bar={CurrentBar}");
            _engine.OnBar(barUtcOpenTime, Instrument.MasterInstrument.Name, open, high, low, close, nowUtc);
            if (logTiming) TraceLifecycle("OnBarUpdate_AFTER_ONBAR", Instrument?.MasterInstrument?.Name, "Historical", _instanceId, $"bar={CurrentBar}");
            LogBarProfileIfSlow("onbar", barProfileSw.ElapsedMilliseconds - barProfileLastMs);
            barProfileLastMs = barProfileSw.ElapsedMilliseconds;
            
            // PATTERN 1: Drive Tick() from bar flow (bar-driven liveness)
            // Tick() is now invoked only when bars arrive, not from a synthetic timer
            // CRITICAL: During State.Historical ("Calculating"), pass bar time so that time-based logic
            // (forced flatten, PRE_HYDRATION transition, range lock at slot time) runs in bar context.
            // Using real time during Historical caused: wrong timeframe range lock, forced flatten
            // firing on every bar (if enabled after 15:55 CT), and "stuck on Calculating". See
            // PRE_HYDRATION_AND_FORCED_FLATTEN_DEEP_DIVE.md.
            var tickTimeUtc = State == State.Historical ? barUtc : nowUtc;
            // Diagnostic: log once per 2 min so we can confirm bar-time fix is deployed (bar_time_used only in Historical) — gated when diagnostic_logging_during_historical=false
            if (!skipHistoricalDiag && _engine != null && (!_lastTickTimeDiagnosticUtc.TryGetValue(Instrument?.MasterInstrument?.Name ?? "", out var lastDiag) || (nowUtc - lastDiag).TotalMinutes >= 2.0))
            {
                _lastTickTimeDiagnosticUtc[Instrument?.MasterInstrument?.Name ?? ""] = nowUtc;
                _engine.LogEngineEvent(nowUtc, "TICK_TIME_SOURCE", new Dictionary<string, object>
                {
                    { "instrument", Instrument?.MasterInstrument?.Name ?? "" },
                    { "state", State.ToString() },
                    { "bar_time_used", State == State.Historical },
                    { "tick_time_utc", tickTimeUtc.ToString("o") },
                    { "note", "Historical=bar time, Realtime=wall clock. Confirms bar-time fix is active." }
                });
            }
            if (logTiming) TraceLifecycle("OnBarUpdate_BEFORE_TICK", Instrument?.MasterInstrument?.Name, "Historical", _instanceId, $"bar={CurrentBar}");
            _engine.Tick(tickTimeUtc);
            if (logTiming) TraceLifecycle("OnBarUpdate_AFTER_TICK", Instrument?.MasterInstrument?.Name, "Historical", _instanceId, $"bar={CurrentBar}");
            LogBarProfileIfSlow("tick", barProfileSw.ElapsedMilliseconds - barProfileLastMs);
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
        
        /// <summary>BE evaluation using price series. When secondary 1-second series enabled: Closes[1][0]. When disabled: Closes[0][0] (primary bar).</summary>
        private void RunBreakEvenCheck()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;
            if (_adapter == null || !_engineReady) return;
            // Use primary series when secondary disabled; otherwise 1-second series for faster BE detection
            var priceBarsIndex = BarsArray.Length >= 2 ? 1 : 0;
            if (CurrentBars[priceBarsIndex] < 1) return;
            decimal currentPrice = (decimal)Closes[priceBarsIndex][0];
            CheckBreakEvenTriggersTickBased(currentPrice, DateTimeOffset.UtcNow);
        }

        /// <summary>Log BAR_PROFILE_SLOW when a section exceeds threshold (chart lag diagnosis). Rate-limited. Only runs when diagnostic_slow_logs=true.</summary>
        private void LogBarProfileIfSlow(string section, long durationMs)
        {
            if (_engine == null || !IsDiagnosticSlowLogsEnabled(_engine)) return;
            if (durationMs < BAR_PROFILE_THRESHOLD_MS) return;
            var instrument = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
            var key = $"{instrument}:{section}";
            var now = DateTimeOffset.UtcNow;
            if (_lastBarProfileLogUtc.TryGetValue(key, out var last) && (now - last).TotalSeconds < BAR_PROFILE_RATE_LIMIT_SECONDS)
                return;
            _lastBarProfileLogUtc[key] = now;
            _engine.LogEngineEvent(now, "BAR_PROFILE_SLOW", new Dictionary<string, object>
            {
                { "instrument", instrument },
                { "section", section },
                { "duration_ms", durationMs },
                { "threshold_ms", BAR_PROFILE_THRESHOLD_MS },
                { "current_bar", CurrentBar },
                { "state", State.ToString() },
                { "note", "Per-bar section exceeded threshold - chart lag diagnosis" }
            });
        }

        /// <summary>
        /// Get execution instrument name for BE monitoring filter (e.g. MGC, MES, MYM).
        /// Must match Intent.ExecutionInstrument - use FullName root, not MasterInstrument.Name (which returns base e.g. GC for MGC).
        /// </summary>
        private string GetExecutionInstrumentForBE()
        {
            if (Instrument == null) return "";
            try
            {
                if (!string.IsNullOrWhiteSpace(Instrument.FullName))
                {
                    var parts = Instrument.FullName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                        return parts[0].ToUpperInvariant();
                }
            }
            catch { }
            return Instrument.MasterInstrument?.Name?.ToUpperInvariant() ?? "";
        }

        /// <summary>
        /// Check break-even triggers for all active intents and modify stop orders when triggered.
        /// Tick-based version: Uses actual tick price instead of bar high/low for immediate detection.
        /// </summary>
        private void CheckBreakEvenTriggersTickBased(decimal tickPrice, DateTimeOffset utcNow)
        {
            // HARDENING FIX 3: Fail closed - don't execute if init failed
            if (_initFailed) return;
            if (_adapter == null || !_engineReady) return;

            // Phase 3.3: Latch price every tick (O(1)); scan runs on cadence only.
            _lastTickPriceForBE = tickPrice;
            if ((utcNow - _lastBeCheckUtc).TotalMilliseconds < BE_SCAN_THROTTLE_MS)
                return; // Skip scan this tick; latch already updated.
            _lastBeCheckUtc = utcNow;

            var beStopwatch = Stopwatch.StartNew();
            try
            {
                // MES/lag fix: use cached list for 200ms so we don't hit journal + lock on every scan.
                var cacheAgeMs = (utcNow - _cachedActiveIntentsForBEUtc).TotalMilliseconds;
                List<(string intentId, Intent intent, decimal beTriggerPrice, decimal entryPrice, decimal? actualFillPrice, string direction)> activeIntents;
                if (_cachedActiveIntentsForBE != null && cacheAgeMs < BE_INTENTS_CACHE_MS)
                {
                    activeIntents = _cachedActiveIntentsForBE;
                }
                else
                {
                    // CRITICAL: Filter by execution instrument (e.g. MGC, MES) - each strategy gets ticks for ONE instrument only.
                    var executionInstrument = GetExecutionInstrumentForBE();
                    activeIntents = _adapter.GetActiveIntentsForBEMonitoring(executionInstrument);
                    _cachedActiveIntentsForBE = activeIntents;
                    _cachedActiveIntentsForBEUtc = utcNow;
                }

                // Rate-limited heartbeat log (minimal payload)
                // This single log unlocks debugging: proves BE logic ran, shows intent count, enables diagnosis
                var instrumentName = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
                var shouldLogBeEval = !_lastBeEvaluationLogUtc.TryGetValue(instrumentName, out var lastBeLogUtc) ||
                                     (utcNow - lastBeLogUtc).TotalSeconds >= BE_EVALUATION_RATE_LIMIT_SECONDS;
                
                // MES/lag fix: minimal payload only; no intent_details (was allocating List + N dicts every second per instrument).
                if (shouldLogBeEval && _engine != null)
                {
                    _lastBeEvaluationLogUtc[instrumentName] = utcNow;
                    _engine.LogEngineEvent(utcNow, "BE_EVALUATION_TICK", new Dictionary<string, object>
                    {
                        { "instrument", instrumentName },
                        { "tick_price", _lastTickPriceForBE },
                        { "active_intent_count", activeIntents.Count },
                        { "note", "BE evaluation ran (rate-limited). active_intent_count=0 means no intents need BE." }
                    });
                }
                
                foreach (var (intentId, intent, beTriggerPrice, entryPrice, actualFillPrice, direction) in activeIntents)
                {
                // Check if BE trigger has been reached using latched price (Phase 3.3: same price used for whole scan)
                bool beTriggerReached = false;
                
                if (direction == "Long")
                {
                    beTriggerReached = _lastTickPriceForBE >= beTriggerPrice;
                }
                else if (direction == "Short")
                {
                    beTriggerReached = _lastTickPriceForBE <= beTriggerPrice;
                }
                else
                {
                    // CRITICAL FIX: Handle invalid direction values
                    if (_engine != null)
                    {
                        _engine.LogEngineEvent(utcNow, "BE_DETECTION_INVALID_DIRECTION", new Dictionary<string, object>
                        {
                            { "intent_id", intentId },
                            { "instrument", intent.Instrument ?? "" },
                            { "stream", intent.Stream ?? "" },
                            { "direction", direction ?? "NULL" },
                            { "tick_price", _lastTickPriceForBE },
                            { "be_trigger_price", beTriggerPrice },
                            { "note", "Invalid direction value - BE detection skipped for this intent. This indicates an intent creation bug." }
                        });
                    }
                    continue; // Skip this intent - invalid direction
                }
                
                if (beTriggerReached)
                {
                    // OPTIONAL ENHANCEMENT: Track when trigger is reached but modification hasn't completed
                    // Check if this is the first time trigger was reached for this intent
                    if (!_beTriggerReachedPendingModification.TryGetValue(intentId, out var triggerReachedAt))
                    {
                        // First time trigger reached - record timestamp
                        _beTriggerReachedPendingModification[intentId] = utcNow;
                        triggerReachedAt = utcNow;
                    }
                    
                    // OPTIONAL ENHANCEMENT: Invariant assertion - if trigger reached for N seconds without modification → ERROR
                    var secondsSinceTriggerReached = (utcNow - triggerReachedAt).TotalSeconds;
                    if (secondsSinceTriggerReached >= BE_TRIGGER_TIMEOUT_SECONDS && _engine != null)
                    {
                        _engine.LogEngineEvent(utcNow, "BE_TRIGGER_TIMEOUT_ERROR", new Dictionary<string, object>
                        {
                            { "intent_id", intentId },
                            { "instrument", intent.Instrument ?? "" },
                            { "stream", intent.Stream ?? "" },
                            { "direction", direction },
                            { "be_trigger_price", beTriggerPrice },
                            { "tick_price", _lastTickPriceForBE },
                            { "seconds_since_trigger_reached", secondsSinceTriggerReached },
                            { "timeout_threshold_seconds", BE_TRIGGER_TIMEOUT_SECONDS },
                            { "note", $"INVARIANT VIOLATION: BE trigger reached {secondsSinceTriggerReached:F1} seconds ago but stop modification has not completed. This indicates a persistent failure in BE modification logic." }
                        });
                    }
                    
                    // CRITICAL FIX: Use breakout level (entryPrice) for BE stop, not actual fill price
                    // "1 tick before breakout point" means 1 tick before the breakout level (entryPrice)
                    // The breakout level is the strategic entry point, slippage shouldn't affect BE stop placement
                    // For stop-market orders: entryPrice = breakout level (brkLong/brkShort)
                    // For limit orders: entryPrice = limit price
                    decimal breakoutLevel = entryPrice;
                    
                    // Calculate break-even stop price (breakout level ± 1 tick)
                    // CRITICAL FIX: Use strategy's instrument tick size directly - don't call Instrument.GetInstrument()
                    // Instrument.GetInstrument() can block/hang if instrument doesn't exist (e.g., M2K)
                    // Strategy's Instrument is already loaded and available - use it as source of truth
                    decimal tickSize = 0.25m; // Default fallback
                    bool tickSizeFallbackUsed = false;
                    try
                    {
                        // Use strategy's instrument tick size (already loaded, no resolution needed)
                        if (Instrument != null && Instrument.MasterInstrument != null)
                        {
                            tickSize = (decimal)Instrument.MasterInstrument.TickSize;
                        }
                        else
                        {
                            tickSizeFallbackUsed = true;
                        }
                    }
                    catch
                    {
                        // Use default fallback if tick size access fails
                        tickSizeFallbackUsed = true;
                    }
                    
                    // CRITICAL FIX: Log warning when tick size fallback is used
                    if (tickSizeFallbackUsed && _engine != null)
                    {
                        _engine.LogEngineEvent(utcNow, "BE_TICK_SIZE_FALLBACK_WARNING", new Dictionary<string, object>
                        {
                            { "intent_id", intentId },
                            { "instrument", intent.Instrument ?? "" },
                            { "stream", intent.Stream ?? "" },
                            { "fallback_tick_size", tickSize },
                            { "be_stop_price", direction == "Long" ? breakoutLevel - tickSize : breakoutLevel + tickSize },
                            { "note", "Tick size fallback used - BE stop price may be incorrect for instruments with non-standard tick sizes. Verify instrument tick size configuration." }
                        });
                    }
                    
                    // CRITICAL FIX: BE stop should be 1 tick before breakout point (breakout level, not fill price)
                    decimal beStopPrice = direction == "Long" 
                        ? breakoutLevel - tickSize  // 1 tick below breakout level for long
                        : breakoutLevel + tickSize; // 1 tick above breakout level for short
                    
                    // Chart lag fix: only attempt BE modify at most once per 200ms per intent. Previously we called
                    // ModifyStopToBreakEven on every Last tick until success → hundreds of account.Orders + NT Change()/sec.
                    var lastAttempt = _lastBeModifyAttemptUtcByIntent.TryGetValue(intentId, out var t) ? t : DateTimeOffset.MinValue;
                    var attemptIntervalMs = (utcNow - lastAttempt).TotalMilliseconds;
                    if (attemptIntervalMs < BE_MODIFY_ATTEMPT_INTERVAL_MS)
                        continue; // Skip this tick; we'll retry when interval has elapsed
                    _lastBeModifyAttemptUtcByIntent[intentId] = utcNow;

                    // Modify stop order to break-even (retry awareness: stop order may not be in account.Orders yet)
                    var modifyResult = _adapter.ModifyStopToBreakEven(intentId, intent.Instrument ?? "", beStopPrice, utcNow);
                    
                    if (modifyResult.Success)
                    {
                        _beTriggerReachedPendingModification.Remove(intentId);
                        _lastBeModifyAttemptUtcByIntent.Remove(intentId); // avoid dict growth
                        
                        // Log successful BE trigger
                        if (_engine != null)
                        {
                            _engine.LogEngineEvent(utcNow, "BE_TRIGGER_REACHED", new Dictionary<string, object>
                            {
                                { "intent_id", intentId },
                                { "instrument", intent.Instrument ?? "" },
                                { "stream", intent.Stream ?? "" },
                                { "direction", direction },
                                { "breakout_level", breakoutLevel },
                                { "actual_fill_price", actualFillPrice },
                                { "be_trigger_price", beTriggerPrice },
                                { "be_stop_price", beStopPrice },
                                { "tick_size", tickSize },
                                { "tick_price", _lastTickPriceForBE },
                                { "detection_method", "TICK_BASED" },
                                { "seconds_to_modify", (utcNow - triggerReachedAt).TotalSeconds },
                                { "note", "Break-even trigger reached (tick-based detection) - stop order modified to break-even using breakout level (1 tick before breakout point)" }
                            });
                        }
                    }
                    else
                    {
                        // Log failure with retry awareness
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
                                { "breakout_level", breakoutLevel },
                                { "actual_fill_price", actualFillPrice },
                                { "be_trigger_price", beTriggerPrice },
                                { "be_stop_price", beStopPrice },
                                { "tick_size", tickSize },
                                { "tick_price", _lastTickPriceForBE },
                                { "detection_method", "TICK_BASED" },
                                { "error", modifyResult.ErrorMessage },
                                { "is_retryable", isRetryableError },
                                { "seconds_since_trigger_reached", secondsSinceTriggerReached },
                                { "note", isRetryableError 
                                    ? "Break-even trigger reached but stop order not found yet (race condition) - will retry on next tick"
                                    : "Break-even trigger reached but stop modification failed" }
                            });
                        }
                    }
                }
                else
                {
                    // Trigger not reached - clear pending tracking if it exists (price moved back below trigger)
                    _beTriggerReachedPendingModification.Remove(intentId);
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
            // Diagnostic: log when BE check is slow (source of MES/instrument slowness)
            if (beStopwatch.ElapsedMilliseconds > 10 && _engine != null && IsDiagnosticSlowLogsEnabled(_engine))
            {
                _engine.LogEngineEvent(DateTimeOffset.UtcNow, "BE_CHECK_SLOW", new Dictionary<string, object>
                {
                    { "duration_ms", beStopwatch.ElapsedMilliseconds },
                    { "instrument", Instrument?.MasterInstrument?.Name ?? "UNKNOWN" },
                    { "tick_price", _lastTickPriceForBE },
                    { "note", "CheckBreakEvenTriggersTickBased exceeded 10ms" }
                });
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
                
                // CRITICAL FIX: Filter orders by instrument to prevent cross-instance order tracking failures
                // When multiple strategy instances run on the same account, all instances receive OrderUpdate
                // callbacks for ALL orders. Each instance should only process orders for its own instrument.
                // This prevents "EXECUTION_UPDATE_UNKNOWN_ORDER" errors when Instance B receives updates
                // for orders submitted by Instance A.
                if (e.Order?.Instrument != Instrument)
                {
                    // FUTURE HARDENING: Warn (not error) if filtered callback occurs
                    // Useful signal if someone misconfigures charts (e.g., same instrument, multiple instances)
                    _filteredOrderUpdateCount++;
                    if (_filteredOrderUpdateCount == 1 || _filteredOrderUpdateCount % 10 == 0)
                    {
                        // Log first occurrence and every 10th occurrence (rate-limited)
                        Log($"WARNING: OrderUpdate filtered - order instrument '{e.Order?.Instrument?.FullName ?? "NULL"}' " +
                            $"does not match strategy instrument '{Instrument?.FullName ?? "NULL"}'. " +
                            $"This may indicate misconfiguration (multiple instances on same instrument). " +
                            $"Filtered count: {_filteredOrderUpdateCount}, InstanceId={_instanceId}", LogLevel.Warning);
                    }
                    return;
                }
                
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
                
                // CRITICAL FIX: Filter executions by instrument to prevent cross-instance order tracking failures
                // When multiple strategy instances run on the same account, all instances receive ExecutionUpdate
                // callbacks for ALL executions. Each instance should only process executions for its own instrument.
                // This prevents fill handling errors when Instance B receives executions for orders submitted by Instance A.
                if (e.Execution?.Order?.Instrument != Instrument)
                {
                    // FUTURE HARDENING: Warn (not error) if filtered callback occurs
                    // Useful signal if someone misconfigures charts (e.g., same instrument, multiple instances)
                    _filteredExecutionUpdateCount++;
                    if (_filteredExecutionUpdateCount == 1 || _filteredExecutionUpdateCount % 10 == 0)
                    {
                        // Log first occurrence and every 10th occurrence (rate-limited)
                        Log($"WARNING: ExecutionUpdate filtered - execution order instrument '{e.Execution?.Order?.Instrument?.FullName ?? "NULL"}' " +
                            $"does not match strategy instrument '{Instrument?.FullName ?? "NULL"}'. " +
                            $"This may indicate misconfiguration (multiple instances on same instrument). " +
                            $"Filtered count: {_filteredExecutionUpdateCount}, InstanceId={_instanceId}", LogLevel.Warning);
                    }
                    return;
                }
                
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
                
                // Forward connection status to health monitor using strongly-typed extension method
                var connectionName = connectionStatusUpdate.Connection?.Options?.Name ?? "Unknown";
                // ConnectionStatusEventArgs - pass the Connection.Status directly (NinjaTrader API)
                // The Connection property has a Status property of type NinjaTrader.Cbi.ConnectionStatus
                var connection = connectionStatusUpdate.Connection;
                var ntStatus = connection?.Status;
                // Use strongly-typed extension method (no reflection needed in strategy project)
                // Null status fallback to ConnectionError
                var healthMonitorStatus = ntStatus != null ? ntStatus.ToHealthMonitorStatus() : QTSW2.Robot.Core.ConnectionStatus.ConnectionError;
                
                // Use RobotEngine's OnConnectionStatusUpdate method
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
        /// Test notifications were removed (HealthMonitor/Pushover). Use log tail on CRITICAL_ENGINE_EVENT for alerting.
        /// </summary>
        public void SendTestNotification()
        {
            Log("Test notification is no longer supported. Alert via log tail on CRITICAL_ENGINE_EVENT.", LogLevel.Information);
        }
    }
}
