using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Diagnostics;
using QTSW2.Robot.Core.Execution;
using QTSW2.Robot.Core.Notifications;
using QTSW2.Robot.Core.SessionAuthority;

/// <summary>
/// Connection recovery state for disconnect/reconnect handling.
/// </summary>
public enum ConnectionRecoveryState
{
    CONNECTED_OK,
    DISCONNECT_FAIL_CLOSED,
    RECONNECTED_RECOVERY_PENDING,
    RECOVERY_RUNNING,
    RECOVERY_COMPLETE
}

public sealed partial class RobotEngine : IExecutionRecoveryGuard
{
    private readonly string _root;
    /// <summary>
    /// Run artifact root: <c>state/</c>, <c>events/</c>, <c>logs/</c>, <c>decisions/</c>, <c>derived/</c> under <see cref="RobotRunArtifactPaths"/>.
    /// Under SIM + journal bypass (e.g. NinjaTrader Playback account): <c>runs/&lt;run_id&gt;/</c> (engine <see cref="_runId"/>).
    /// </summary>
    private string _persistenceBase;
    /// <summary>True when this process uses an isolated run tree under <c>runs/</c> (SIM + ignore existing stream journals).</summary>
    private bool _isolatedPlaybackPersistence;
    private readonly RobotLogger _log; // Kept for backward compatibility during migration
    private RobotLoggingService? _loggingService; // New async logging service (Fix B); may rebind when playback isolation changes log root
    /// <summary>Stream + execution journal store. Rebound in <see cref="Start"/> when isolated playback is enabled.</summary>
    private JournalStore _journals;
    private readonly TimetableFilePoller _timetablePoller;
    private readonly object _engineLock = new object(); // Serialize engine entrypoints (Tick/OnBar/etc.)

    // Engine run identifier (GUID per engine Start())
    private string? _runId;
    private DateTimeOffset _engineStartUtc = DateTimeOffset.MinValue;
    /// <summary>Stable id for reconciliation single-writer / debounce (per strategy instance, process-wide coordinator).</summary>
    private readonly string _reconciliationWriterInstanceId = "eng:" + Guid.NewGuid().ToString("N");
    private DateTimeOffset _lastAssembleMismatchDiagUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAssembleMismatchThreadAttrUtc = DateTimeOffset.MinValue;
    private readonly Dictionary<string, DateTimeOffset> _ledgerReconciliationAuthorityLogThrottle =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Idle assemble path: lengthen proof-diag cooldown (no mismatch / no recovery / fast assemble).</summary>
    private const int AssembleMismatchDiagQuietSeconds = 180;

    /// <summary>Busy or potentially material path: keep prior ~55s heartbeat.</summary>
    private const int AssembleMismatchDiagBusySeconds = 55;
    private const int LedgerReconciliationAuthorityLogQuietSeconds = 30;

    private sealed class IeaUnavailableDegradedSuppressState
    {
        public int BrokerQty;
        public int JournalQty;
        public int BrokerWorking;
        public long ActivityGeneration;
        public DateTimeOffset AnchorUtc;
    }

    private readonly Dictionary<string, IeaUnavailableDegradedSuppressState> _ieaUnavailableDegradedSuppressByInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    private const double IeaDegradedSuppressResurfaceSeconds = 45.0;

    private sealed class NonOwnerAssembleSuppressState
    {
        public int BrokerQty;
        public int JournalQty;
        public int BrokerWorking;
        public long ActivityGeneration;
        public DateTimeOffset AnchorUtc;
    }

    private readonly Dictionary<string, NonOwnerAssembleSuppressState> _nonOwnerAssembleSuppressByInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last-observed <see cref="JournalParityStatus.PARITY_PENDING_ALIGNMENT"/> per instrument (for transition logs).</summary>
    private readonly ConcurrentDictionary<string, bool> _pendingAlignmentActiveByInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    private const double NonOwnerAssembleSuppressResurfaceSeconds = 45.0;

    private RuntimeAuditHub? _runtimeAudit;
    private ReconciliationConvergenceTracker? _reconciliationConvergence;

    private ParitySpec? _spec;
    private TimeService? _time;

    private string _specPath = "";
    private string _timetablePath = "";
    private string _executionPolicyPath = ""; // PHASE 4: Execution policy file path
    private ExecutionPolicy? _executionPolicy; // PHASE 4: Loaded execution policy

    private readonly string? _executionInstrument; // Execution instrument from constructor (MES, ES, etc.)
    private readonly string? _masterInstrumentName; // MasterInstrument.Name from NinjaTrader (for explicit canonical matching)

    private string? _lastTimetableHash;
    /// <summary>Publisher <c>timetable_hash</c> when present, else content hash from poll.</summary>
    private string? _currentTimetableHash;
    /// <summary>Publisher <c>version_timestamp</c> when present.</summary>
    private string? _currentTimetableVersionTimestamp;
    private TimetableContract? _lastTimetable; // Store timetable for application after trading date is locked
    private DateOnly? _activeTradingDate; // PHASE 3: Store as DateOnly (authoritative), convert to string only for logging/journal
    private HashSet<string>? _eligibleSet; // Streams with timetable.enabled==true for this session (from timetable_current.json)

    /// <summary>
    /// Get trading date as string for logging/journal purposes.
    /// Returns empty string if trading date is not yet set.
    /// </summary>
    private string TradingDateString => _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
    private string OwnershipAccountKey => string.IsNullOrWhiteSpace(_accountName) ? "default" : _accountName.Trim();

    /// <summary>
    /// Convert UTC to Chicago timezone. For strategy layer (e.g. SessionCloseResolver).
    /// </summary>
    public DateTimeOffset ConvertUtcToChicago(DateTimeOffset utcTime)
    {
        lock (_engineLock) { return _time?.ConvertUtcToChicago(utcTime) ?? utcTime; }
    }

    /// <summary>
    /// Get parity spec for SessionCloseResolver (strategy layer). Returns null if not loaded.
    /// </summary>
    public ParitySpec? GetParitySpec()
    {
        lock (_engineLock) { return _spec; }
    }

    /// <summary>
    /// Get current trading date (for external access, e.g., NinjaTrader strategy).
    /// Returns empty string if trading date is not yet set.
    /// </summary>
    public string GetTradingDate()
    {
        lock (_engineLock)
        {
            return TradingDateString;
        }
    }

    /// <summary>
    /// Get execution instrument (e.g., MNQ, MGC) for IEA routing and BE monitoring.
    /// Resolves chart instrument to execution instrument via policy: CL→MCL, NQ→MNQ, etc.
    /// CRITICAL: Intents have ExecutionInstrument from streams (MCL, MNQ). BE filter must match.
    /// </summary>
    public string GetExecutionInstrument()
    {
        var chartOrCanonical = _executionInstrument ?? "";
        if (string.IsNullOrWhiteSpace(chartOrCanonical)) return "";
        if (_executionPolicy != null)
        {
            var resolved = _executionPolicy.GetEnabledExecutionInstrument(chartOrCanonical);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
        }
        return chartOrCanonical;
    }

    /// <summary>
    /// Pre-load execution journal cache for trading date.
    /// Call on Realtime transition so BE monitoring never hits disk on first lookup.
    /// </summary>
    public void WarmExecutionJournalCacheForTradingDate(string tradingDate)
    {
        if (string.IsNullOrWhiteSpace(tradingDate)) return;
        _executionJournal.WarmCacheForTradingDate(tradingDate);
    }


    private readonly Dictionary<string, StreamStateMachine> _streams = new();
    private readonly ExecutionMode _executionMode;
    /// <summary>
    /// When true (e.g. NinjaTrader Playback account): do not load existing stream journal files — fresh in-memory slot state per run.
    /// Normal SIM loads <c>state/stream_journals</c> for restart recovery.
    /// </summary>
    private readonly bool _ignoreExistingStreamJournals;
    /// <summary>NinjaTrader-only: account name contained "Playback" at engine construction — for startup diagnostics.</summary>
    private readonly bool _playbackAccountDetected;
    /// <summary>NinjaTrader playback: set before <see cref="Start"/> so timetable/stream initialization uses chart time instead of wall clock.</summary>
    private DateTimeOffset? _playbackStartTimeUtc;

    // Session start time per instrument (from TradingHours, fallback to 17:00 CST)
    private readonly Dictionary<string, string> _sessionStartTimes = new Dictionary<string, string>();

    // SessionCloseResolver: cache per (tradingDay, sessionClass)
    private readonly Dictionary<(string tradingDay, string sessionClass), SessionCloseResult> _sessionCloseResults = new();

    /// <summary>Optional <c>configs/robot/session_calendar.json</c> — when rows exist for (day, calendar_group), authority for session close supersedes NT cache in <see cref="GetSessionCloseResultOrFallback"/>.</summary>
    private readonly SessionPolicyService? _sessionPolicyService;
    private readonly string _sessionCalendarPath;
    private DateOnly? _internalCalendarPolicyMaterializedForDay;
    private readonly Dictionary<(string tradingDay, string sessionClass), SessionCloseResult> _internalCalendarSessionClose = new();
    private readonly HashSet<(string tradingDay, string sessionClass)> _sessionCloseEmittedKeys = new();

    // Session index per sessionClass for re-entry gate (S1, S2)
    private readonly Dictionary<string, (string tradingDay, int index)> _currentSessionKeyByClass = new();
    private IExecutionAdapter? _executionAdapter;
    private RiskGate? _riskGate;
    private ExecutionJournal _executionJournal;
    /// <summary>Transient fill observations so mismatch assembly does not classify <see cref="MismatchType.BROKER_AHEAD"/> while journal disk read lags <see cref="ExecutionJournal.RecordEntryFill"/>.</summary>
    private readonly PendingFillBridge _pendingFillBridge;
    /// <summary>Canonical ownership ledger (P1 refactor). Dual-run alongside existing stores when feature flag is enabled.</summary>
    private InstrumentOwnershipLedger? _ownershipLedger;
    /// <summary>Unified execution authority (P2 refactor). Facade over existing gates.</summary>
    private UnifiedExecutionAuthority? _unifiedAuthority;
    /// <summary>Durable orphan fill journal (P4 refactor).</summary>
    private OrphanFillJournal? _orphanFillJournal;
    /// <summary>Durable ownership event journal (P6). Monotonic sequence for all ownership mutations.</summary>
    private OwnershipEventJournal? _ownershipEventJournal;
    /// <summary>Authoritative state snapshot emitter (P5 refactor).</summary>
    private AuthoritativeStateEmitter? _stateEmitter;
    private InstrumentIntentCoordinator? _intentExposureCoordinator;
    private ExecutionEventWriter _eventWriter = null!;
    private ReconciliationRunner? _reconciliationRunner;
    private ReconciliationClassifier? _reconciliationClassifier;
    private ReconciliationRepairExecutor? _reconciliationRepairExecutor;
    private volatile Dictionary<string, (int AccountQty, int JournalQty)>? _lastRunnerQtyByInstrument;
    private ProtectiveCoverageCoordinator? _protectiveCoordinator;
    private MismatchEscalationCoordinator? _mismatchCoordinator;
    private KillSwitch? _killSwitch;
    private readonly ExecutionSummary _executionSummary;
    private HealthMonitor? _healthMonitor; // Optional: health monitoring and alerts

    // Guard against multiple execution policy failure notifications per startup
    private bool _executionPolicyFailureReported = false;

    // Logging configuration
    private readonly LoggingConfig _loggingConfig;
    private string _resolvedLogDir;
    private string _resolvedLogDirSource;
    private readonly string _loggingConfigPath;
    private string? _resolvedLogDirWarning;
    /// <summary>Absolute <c>logs/robot</c> directory in use (may match isolated <see cref="_persistenceBase"/>).</summary>
    private string _activeRobotLogDir;
    private readonly bool _useAsyncLogging;


    // Engine heartbeat tracking for liveness monitoring
    private DateTimeOffset _lastTickUtc = DateTimeOffset.MinValue;

    // Gap violation summary tracking (rate-limited)
    private DateTimeOffset? _lastGapViolationSummaryUtc = null;

    // Stream status summary (rate-limited, diagnostic only)
    private DateTimeOffset? _lastStreamStatusSummaryUtc = null;
    private const double STREAM_STATUS_SUMMARY_INTERVAL_MINUTES = 5.0;

    // Forced flatten skip diagnostic (rate-limited) — when session close not cached
    private DateTimeOffset? _lastForcedFlattenSkipLogUtc = null;
    private const double FORCED_FLATTEN_SKIP_LOG_INTERVAL_MINUTES = 5.0;

    // Session close cache missing alert — first time we detect cache empty in forced flatten block
    private DateTimeOffset? _firstSessionCloseCacheMissUtc = null;
    private const double SESSION_CLOSE_CACHE_MISSING_ALERT_MINUTES = 5.0;

    // One-shot: emit once per (tradingDay, sessionClass), suppress until next trading day
    private readonly HashSet<(string, string)> _sessionCloseFallbackEmittedKeys = new();
    private readonly HashSet<(string, string)> _sessionCloseCacheMissingEmittedKeys = new();
    private readonly HashSet<(string, string)> _sessionCloseFallbackFailedEmittedKeys = new();
    private readonly object _sessionCloseGlobalSweepLock = new();
    private readonly HashSet<(string tradingDay, string instrument)> _sessionCloseGlobalSweepRequested = new();
    private readonly HashSet<(string tradingDay, string sessionClass, string instrument, string reason)> _sessionCloseGlobalSweepSkipLogged = new();

    /// <summary>Watchdog session/flatten visibility: highest source wins per (tradingDay, sessionClass).</summary>
    private readonly Dictionary<(string tradingDay, string sessionClass), string> _sessionFlattenVisibilitySource = new();
    private readonly HashSet<(string tradingDay, string sessionClass, string source)> _sessionTruthOverrideEmittedKeys = new();

    /// <summary>SESSION_CLOSE_FALLBACK_WARNING: once per (tradingDay, sessionClass).</summary>
    private readonly HashSet<(string, string)> _sessionCloseFallbackWarningEmitted = new();

    // Event-driven snapshot (strict 60s rate limit, independent of periodic)
    private DateTimeOffset? _lastEventDrivenSnapshotUtc = null;
    private const double EVENT_DRIVEN_SNAPSHOT_RATE_LIMIT_SECONDS = 60.0;

    // PHASE 3.1: Identity invariants status tracking (rate-limited)
    private DateTimeOffset? _lastIdentityInvariantsCheckUtc = null;
    private const int IDENTITY_INVARIANTS_CHECK_INTERVAL_SECONDS = 60; // Check every 60 seconds
    private bool _lastIdentityInvariantsPass = true; // Track last status for on-change emission

    // Account/environment info for startup banner (set by strategy host)
    private string? _accountName;
    private string? _environment;

    // Instruments frozen due to RECONCILIATION_QTY_MISMATCH or FLATTEN_FAILED_PERSISTENT (block execution; allow range building)
    private readonly HashSet<string> _frozenInstruments = new(StringComparer.OrdinalIgnoreCase);
    private RiskLatchManager? _riskLatchManager;

    /// <summary>Suppresses redundant release-readiness evaluation and periodic reconciliation when inputs are stable.</summary>
    private readonly ReleaseReconciliationRedundancySuppression _releaseReconRedundancy = new();

    /// <summary>Set when <see cref="JournalIntegrityGuarantee.EnsureJournalIntegrity"/> ran during release input build (post-eval invariant).</summary>
    private readonly ConcurrentDictionary<string, byte> _journalIntegrityEnsuredForInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record JournalIntegrityInvariantDebounceState(
        string Status,
        int BrokerQty,
        int JournalQty,
        DateTimeOffset FirstSeenUtc,
        DateTimeOffset LastSeenUtc,
        int SeenCount,
        bool CriticalEmitted);

    private readonly ConcurrentDictionary<string, JournalIntegrityInvariantDebounceState> _journalIntegrityInvariantDebounceByInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    private const double JournalIntegrityInvariantCriticalAfterMilliseconds = 2000.0;

    private sealed class JournalParityRegistryViewImpl : IJournalParityRegistryView
    {
        public bool UseInstrumentExecutionAuthority { get; init; }
        public int IeaOwnedPlusAdoptedWorking { get; init; }
    }

    // Disconnect recovery state machine
    private ConnectionRecoveryState _recoveryState = ConnectionRecoveryState.CONNECTED_OK;
    private DateTimeOffset? _disconnectFirstUtc;
    private DateTimeOffset? _recoveryStartedUtc;
    private DateTimeOffset? _recoveryCompletedUtc;
    private DateTimeOffset? _secondReconciliationRunUtc; // Lightweight second reconciliation after recovery
    private const int SECOND_RECONCILIATION_DELAY_MINUTES = 5;
    private ConnectionStatus _lastConnectionStatus = ConnectionStatus.Connected;

    /// <summary>
    /// True when broker connection is confirmed. Used to suppress reconciliation before connection is ready.
    /// </summary>
    private bool IsBrokerConnected => _lastConnectionStatus == ConnectionStatus.Connected;

    // Broker sync gate timestamps (for recovery synchronization)
    private DateTimeOffset? _lastOrderUpdateUtc;
    private DateTimeOffset? _lastExecutionUpdateUtc;
    private DateTimeOffset _lastEngineTickUtc = DateTimeOffset.MinValue;
    private DateTimeOffset? _reconnectUtc;
    private DateTimeOffset? _lastSyncWaitLogUtc; // Rate-limiting for DISCONNECT_RECOVERY_WAITING_FOR_SYNC

    // ENGINE_TICK_CALLSITE rate limit: log at most once per 5 seconds to prevent log flooding during Historical
    // (Watchdog uses this for liveness; 5s is sufficient. During Historical, thousands of ticks would otherwise flood queue.)
    private DateTimeOffset? _lastEngineTickCallsiteLogUtc = null;
    private const double ENGINE_TICK_CALLSITE_RATE_LIMIT_SECONDS = 5.0;

    // Diagnostic: one-time log when we skip timetable poll during Historical (confirms fix is deployed)
    private bool _loggedHistoricalPollSkip = false;

    // TimetableCache instrumentation: rate-limited HIT/REFRESH logs for validation
    private DateTimeOffset? _lastTimetableCacheHitLogUtc;
    private DateTimeOffset? _lastTimetableCacheRefreshLogUtc;
    private const double TIMETABLE_CACHE_LOG_RATE_LIMIT_SECONDS = 60.0;

    // Recovery runner guard (prevent re-entrancy)
    private bool _recoveryRunnerActive = false;
    private readonly object _recoveryLock = new object();

    // Throttle repeated RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT logs per (instrument, brokerWorking, localWorking)
    private readonly Dictionary<string, DateTimeOffset> _recoveryAttemptLogThrottle = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
    private const int RecoveryAttemptLogThrottleSeconds = 60;

    // Throttle RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH per instrument (broker working > 0 but zero OWNED+ADOPTED live in registry)
    private readonly Dictionary<string, DateTimeOffset> _reconciliationBrokerWorkingOwnedInvariantThrottle = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
    private const int ReconciliationBrokerWorkingOwnedInvariantThrottleSeconds = 60;

    // Timer-based engine heartbeat (watchdog liveness when market closed, no ticks)
    private Timer? _engineHeartbeatTimer;
    private int _shutdownRequested;
    private string? _heartbeatTradingDateCache; // Lock-free read from timer callback
    private int _engineHeartbeatWallTick;
    private DateTimeOffset _playbackStallQuiesceEligibleSinceUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _playbackStallQuiesceStopRequestedUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _playbackStallQuiesceExposureDeferLastLogUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _playbackStallQuiesceExposureDeferFirstUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _playbackStallQuiesceIeaDeferLastLogUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _playbackStallQuiesceIeaDeferFirstUtc = DateTimeOffset.MinValue;
    private int _playbackStallQuiesceArmed;
    private int _playbackStallQuiesceStopRequested;
    private int _playbackStallQuiesceForceFinalizeRequested;
    private int _connectivityShutdownStopRequested;
    private int _runWideShutdownStopRequested;
    private const int PlaybackStallQuiesceGraceSeconds = 12;
    private const int PlaybackStallQuiesceForceFinalizeSeconds = 45;
    private const int PlaybackStallQuiesceIeaStaleReleaseSeconds = 30;
    private const int PlaybackStallQuiesceLiveExposureForceFinalizeSeconds = 120;
    private int _shutdownCompleted;

    private DateTimeOffset _lastAuditMetricsEmitWallUtc = DateTimeOffset.MinValue;
    private const double AuditMetricsEmitWallIntervalSeconds = 10.0;

    /// <summary>
    /// Set account and environment info for startup banner.
    /// Called by strategy host before Start().
    /// </summary>
    public void SetAccountInfo(string? accountName, string? environment)
    {
        lock (_engineLock)
        {
            _accountName = accountName;
            _environment = environment;
        }
    }

    /// <summary>
    /// NinjaTrader playback: anchor <see cref="Start"/> timetable/stream initialization to chart bar time instead of wall clock.
    /// Call after construction and before <see cref="Start"/> (e.g. from <c>State.DataLoaded</c> on a NinjaTrader Playback account).
    /// Does not affect <see cref="Tick"/> — tick events still carry their own timestamps.
    /// </summary>
    public void SetPlaybackStartTime(DateTimeOffset playbackAnchorUtc)
    {
        _playbackStartTimeUtc = playbackAnchorUtc;
    }


    /// <summary>
    /// Get last tick timestamp for liveness monitoring.
    /// </summary>
    public DateTimeOffset GetLastTickUtc()
    {
        lock (_engineLock)
        {
            return _lastTickUtc;
        }
    }

    /// <summary>
    /// Check if execution is allowed based on recovery state and global kill switch.
    /// Execution is allowed only when: CONNECTED_OK or RECOVERY_COMPLETE, AND global kill switch is off.
    /// Phase 5: Kill switch blocks protectives and modifications (same path as entry orders).
    /// </summary>
    public bool IsExecutionAllowed()
    {
        lock (_engineLock)
        {
            if (_killSwitch != null && _killSwitch.IsEnabled())
                return false;
            return _recoveryState == ConnectionRecoveryState.CONNECTED_OK ||
                   _recoveryState == ConnectionRecoveryState.RECOVERY_COMPLETE;
        }
    }

    /// <summary>
    /// Get current recovery state (for RiskGate reason).
    /// </summary>
    public ConnectionRecoveryState RecoveryState
    {
        get
        {
            lock (_engineLock)
            {
                return _recoveryState;
            }
        }
    }

    // IExecutionRecoveryGuard implementation
    bool IExecutionRecoveryGuard.IsExecutionAllowed() => IsExecutionAllowed();

    string IExecutionRecoveryGuard.GetRecoveryStateReason() => RecoveryState.ToString();

    public RobotEngine(string projectRoot, TimeSpan timetablePollInterval, ExecutionMode executionMode = ExecutionMode.DRYRUN, string? customLogDir = null, string? customTimetablePath = null, string? instrument = null, string? masterInstrumentName = null, bool ignoreExistingStreamJournals = false, bool playbackAccountDetected = false, bool useAsyncLogging = true)
    {
        _root = projectRoot;
        _persistenceBase = projectRoot;
        EngineCpuProfile.SetRoot(projectRoot);
        _executionMode = executionMode;
        _ignoreExistingStreamJournals = ignoreExistingStreamJournals;
        _playbackAccountDetected = playbackAccountDetected;
        _executionInstrument = instrument;
        _masterInstrumentName = masterInstrumentName; // Store MasterInstrument.Name for explicit canonical matching

        // Log the instrument passed from NinjaTrader (for debugging)
        if (!string.IsNullOrWhiteSpace(instrument))
        {
            // Note: Can't use LogEvent here because logger isn't initialized yet
            // This will be logged later in Start() after logger is ready
        }

        // Load logging configuration (filters, queues, rotation limits; physical paths are run-scoped under _persistenceBase).
        _loggingConfig = LoggingConfig.LoadFromFile(projectRoot, emergencyRobotLogDir: null, suppressEmergencyWrite: true);
        _loggingConfigPath = Path.Combine(projectRoot, "configs", "robot", "logging.json");

        // Run-authoritative robot JSONL: isolated playback defers physical init until Start() → runs/<run_id>/logs/robot (never projectRoot/logs/robot).
        var effectiveLogDir = RobotRunArtifactPaths.LogsRobot(_persistenceBase);
        var deferPlaybackRobotLogs = executionMode == ExecutionMode.SIM && ignoreExistingStreamJournals;
        string? warning = null;
        if (!deferPlaybackRobotLogs)
        {
            try
            {
                Directory.CreateDirectory(effectiveLogDir);
            }
            catch (Exception ex)
            {
                warning = $"Failed to create log dir '{effectiveLogDir}'. Error: {ex.Message}";
                try { Directory.CreateDirectory(effectiveLogDir); } catch { /* ignore */ }
            }
        }

        _resolvedLogDir = effectiveLogDir;
        _resolvedLogDirSource = deferPlaybackRobotLogs ? "deferred_until_start:isolated_playback" : "persistence_base:logs/robot";
        _resolvedLogDirWarning = warning;
        _activeRobotLogDir = _resolvedLogDir;
        _useAsyncLogging = useAsyncLogging;

        if (deferPlaybackRobotLogs)
        {
            _loggingService = null;
            _log = new RobotLogger(projectRoot, null, instrument, null, deferPhysicalInitUntilRebind: true);
        }
        else
        {
            if (useAsyncLogging)
                _loggingService = RobotLoggingService.GetOrCreate(projectRoot, _resolvedLogDir);

            _log = new RobotLogger(projectRoot, _resolvedLogDir, instrument, _loggingService);
        }

        _journals = new JournalStore(_persistenceBase);
        _timetablePoller = new TimetableFilePoller(timetablePollInterval);

        _specPath = Path.Combine(_root, "configs", "analyzer_robot_parity.json");
        _timetablePath = ResolveDefaultTimetablePath(_root, customTimetablePath, _playbackAccountDetected);
        _sessionCalendarPath = Path.Combine(_root, "configs", "robot", "session_calendar.json");
        _sessionPolicyService = SessionPolicyService.TryLoad(_sessionCalendarPath);
        var policyFromEnv = Environment.GetEnvironmentVariable("QTSW2_EXECUTION_POLICY_PATH");
        _executionPolicyPath = string.IsNullOrWhiteSpace(policyFromEnv)
            ? Path.Combine(_root, "configs", "execution_policy.json")
            : (Path.IsPathRooted(policyFromEnv)
                ? policyFromEnv
                : Path.Combine(_root, policyFromEnv.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        // Live execution arms from data/timetable/timetable_current.json (matrix-published); see _eligibleSet.

        // NOTE: KillSwitch logs during construction. To ensure ALL logs include run_id,
        // we delay KillSwitch creation until Start() after _runId is set on the logger.
        // RULE: ExecutionJournal ctor must not log — RobotLogger may block writes until Start() RebindLogging (isolated playback).
        _executionJournal = new ExecutionJournal(_persistenceBase, _log);
        _pendingFillBridge = new PendingFillBridge(_log);
        JournalParityPendingLedger.Clear();
        WireExecutionJournalReleaseSuppressionCallbacks();
        _executionSummary = new ExecutionSummary();
        // ExecutionEventWriter: created in Start() after RebindPersistenceIfNeeded so canonical events use _persistenceBase (isolated playback tree when enabled).
    }

    /// <summary>
    /// Gap 5: Canonical execution event writer for replay/audit. Subsystems emit via this.
    /// </summary>
    public ExecutionEventWriter EventWriter => _eventWriter;


    private static string SanitizeRunIdForPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        var filtered = new string(s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        if (filtered.Length == 0) return "";
        return filtered.Length > 128 ? filtered.Substring(0, 128) : filtered;
    }

    /// <summary>Optional env <c>QTSW2_RUN_ID</c> for stable playback folder names (alphanumeric, _, -).</summary>
    private void ApplyOptionalRunIdFromEnvironment()
    {
        var s = SanitizeRunIdForPath(Environment.GetEnvironmentVariable("QTSW2_RUN_ID"));
        if (s.Length > 0)
            _runId = s;
    }

    /// <summary>
    /// Isolated SIM (Playback): reuse <c>QTSW2_RUN_ID</c> across all <see cref="RobotEngine"/> instances in the process so
    /// <c>runs/&lt;run_id&gt;/</c> is shared. First engine generates and sets the env var (only when unset); live/other modes keep a unique Guid per engine.
    /// </summary>
    /// <param name="assignmentSource">Diagnostic: GENERATED_NEW, ENV_SHARED, ENV_INVALID_REGENERATED, or LIVE_UNIQUE.</param>
    private void AssignRunIdAtSessionStart(out string assignmentSource)
    {
        var isolatedPlayback = _executionMode == ExecutionMode.SIM && _ignoreExistingStreamJournals;
        if (!isolatedPlayback)
        {
            _runId = Guid.NewGuid().ToString("N");
            assignmentSource = "LIVE_UNIQUE";
            return;
        }

        var existingRunId = Environment.GetEnvironmentVariable("QTSW2_RUN_ID");
        if (!string.IsNullOrWhiteSpace(existingRunId))
        {
            var s = SanitizeRunIdForPath(existingRunId);
            if (s.Length > 0)
            {
                _runId = s;
                assignmentSource = "ENV_SHARED";
            }
            else
            {
                _runId = Guid.NewGuid().ToString("N");
                try
                {
                    Environment.SetEnvironmentVariable("QTSW2_RUN_ID", _runId);
                }
                catch
                {
                    /* best-effort */
                }

                assignmentSource = "ENV_INVALID_REGENERATED";
            }
        }
        else
        {
            _runId = Guid.NewGuid().ToString("N");
            try
            {
                Environment.SetEnvironmentVariable("QTSW2_RUN_ID", _runId);
            }
            catch
            {
                /* best-effort */
            }

            assignmentSource = "GENERATED_NEW";
        }
    }

    private string ComputeNextPersistenceBase(out bool isolatedPlayback)
    {
        isolatedPlayback = _executionMode == ExecutionMode.SIM && _ignoreExistingStreamJournals;
        if (!isolatedPlayback)
            return _root;
        var folder = string.IsNullOrWhiteSpace(_runId) ? "unknown_run" : _runId.Trim();
        var path = Path.Combine(_root, "runs", folder);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Coordinator scope only: execution instruments for active (non-committed) streams, union instruments with open journal exposure on this engine (includes committed streams still carrying open entries).
    /// Open journal rows are included only when (trading_date + stream/session + run persistence context) align with this engine.
    /// Does not alter IEA, registry, or broker mutation ownership.
    /// </summary>
    private IReadOnlyList<string> GetEngineScopedExecutionInstrumentKeys()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_engineLock)
        {
            foreach (var s in _streams.Values)
            {
                if (s.Committed) continue;
                var ex = s.ExecutionInstrument?.Trim();
                if (!string.IsNullOrEmpty(ex))
                    set.Add(ex);
            }
        }

        foreach (var kvp in _executionJournal.GetOpenJournalEntriesByInstrument())
        {
            if (kvp.Value == null || kvp.Value.Count == 0) continue;
            foreach (var (td, stream, _, entry) in kvp.Value)
            {
                if (!IsOpenJournalRowInEngineCoordinatorScope(td, stream, entry))
                    continue;
                var jk = kvp.Key?.Trim();
                if (!string.IsNullOrEmpty(jk))
                {
                    set.Add(jk);
                    break;
                }
            }
        }

        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Late-run quiescence scope: only instruments that still have genuinely live stream work.
    /// Includes interrupted session-close streams so broker-flat-gated reentry stays protected.
    /// Excludes committed journal-only residue.
    /// </summary>
    private IReadOnlyList<string> GetLiveRecoveryExecutionInstrumentKeys()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_engineLock)
        {
            foreach (var s in _streams.Values)
            {
                if (s.Committed && !s.ExecutionInterruptedByClose)
                    continue;

                var ex = s.ExecutionInstrument?.Trim();
                if (!string.IsNullOrEmpty(ex))
                    set.Add(ex);
            }
        }

        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private bool IsExecutionInstrumentInLiveRecoveryScope(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return false;
        var key = instrument.Trim();
        lock (_engineLock)
        {
            foreach (var s in _streams.Values)
            {
                if (s.Committed && !s.ExecutionInterruptedByClose)
                    continue;
                if (string.Equals(s.ExecutionInstrument?.Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private bool IsExecutionInstrumentInThisEngineScope(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return false;
        var key = instrument.Trim();
        lock (_engineLock)
        {
            foreach (var s in _streams.Values)
            {
                if (s.Committed) continue;
                if (string.Equals(s.ExecutionInstrument?.Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        var canon = GetCanonicalInstrument(key);
        foreach (var kvp in _executionJournal.GetOpenJournalEntriesByInstrument())
        {
            if (kvp.Value == null || kvp.Value.Count == 0) continue;
            if (!ExecutionJournal.OpenJournalMapBucketMatches(kvp.Key, key, canon))
                continue;
            foreach (var (td, stream, _, entry) in kvp.Value)
            {
                if (IsOpenJournalRowInEngineCoordinatorScope(td, stream, entry))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// When isolated playback uses <c>runs/&lt;run_id&gt;/</c>, require journal reads to stay tied to that folder (tail segment matches <see cref="_runId"/>).
    /// </summary>
    private bool IsPersistenceAlignedWithRunContext()
    {
        if (!_isolatedPlaybackPersistence)
            return true;
        if (string.IsNullOrWhiteSpace(_runId))
            return true;
        try
        {
            var full = Path.GetFullPath(_persistenceBase).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var tail = Path.GetFileName(full);
            return string.Equals(tail, _runId.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Open journal rows count for coordinator scope when trading day, stream/session (via live <see cref="StreamStateMachine"/> when present), and run persistence context align.
    /// </summary>
    private bool IsOpenJournalRowInEngineCoordinatorScope(string tradingDateFromFile, string streamId, ExecutionJournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(streamId))
            return false;
        if (!_activeTradingDate.HasValue)
            return false;
        if (!IsPersistenceAlignedWithRunContext())
            return false;

        var sid = streamId.Trim();
        var tdFile = tradingDateFromFile?.Trim() ?? "";

        // Durable untracked-fill bucket: filename uses RECOVERY, not a calendar yyyy-MM-dd; scope to active engine + run folder only.
        if (string.Equals(sid, ExecutionJournal.UntrackedFillRecoveryStream, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tdFile, ExecutionJournal.UntrackedFillRecoveryTradingDateBucket, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(tdFile, TradingDateString, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(entry.TradingDate) &&
            !string.Equals(entry.TradingDate.Trim(), tdFile, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(entry.Stream) &&
            !string.Equals(entry.Stream.Trim(), sid, StringComparison.OrdinalIgnoreCase))
            return false;

        lock (_engineLock)
        {
            if (_streams.TryGetValue(sid, out var sm))
            {
                if (!string.Equals(sm.TradingDate, TradingDateString, StringComparison.OrdinalIgnoreCase))
                    return false;
                // Session is implicit: sm.Session is this engine's session class for this stream id.
                return true;
            }
        }

        // Integrity-recovered synthetic stream (strategy stream id not in _streams); calendar day already matched.
        if (string.Equals(sid, ExecutionJournal.RecoveredIntentStream, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool RobotLogDirectoriesEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Align async <see cref="RobotLoggingService"/> + <see cref="RobotLogger"/> with <see cref="RobotRunArtifactPaths.LogsRobot"/> after <see cref="_persistenceBase"/> changes.
    /// </summary>
    private void RebindRobotLogDirectoryIfNeeded()
    {
        var target = RobotRunArtifactPaths.LogsRobot(_persistenceBase);
        if (RobotLogDirectoriesEqual(target, _activeRobotLogDir))
            return;

        if (_useAsyncLogging && _loggingService != null)
        {
            _loggingService.Release();
            _loggingService = null;
        }

        if (_useAsyncLogging)
            _loggingService = RobotLoggingService.GetOrCreate(_root, target);

        _log.RebindLogging(_loggingService, target);
        _activeRobotLogDir = RobotRunArtifactPaths.LogsRobot(_persistenceBase);
        _resolvedLogDir = _activeRobotLogDir;
        _resolvedLogDirSource = _isolatedPlaybackPersistence ? "run_scoped:logs/robot" : "persistence_base:logs/robot";

        if (_isolatedPlaybackPersistence && !string.IsNullOrWhiteSpace(_runId))
        {
            try
            {
                var full = Path.GetFullPath(target);
                var rid = _runId.Trim();
                if (full.IndexOf(rid, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    var utcNow = _playbackStartTimeUtc ?? DateTimeOffset.UtcNow;
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "LOG_DIR_INVALID", state: "ENGINE",
                        new
                        {
                            active_robot_log_dir = target,
                            run_id = rid,
                            note = "Isolated playback requires robot logs under runs/<run_id>/logs/robot"
                        }));
                    throw new InvalidOperationException(
                        $"Robot logs directory is not run-scoped (missing run_id '{rid}' in path): '{full}'");
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch
            {
                /* best-effort validation only */
            }
        }
    }

    private void WireExecutionJournalReleaseSuppressionCallbacks()
    {
        void NotifyReleaseSuppressionActivity()
        {
            _releaseReconRedundancy.NotifyExecutionActivity();
            _mismatchCoordinator?.NotifyReconciliationAuditWake();
        }
        _executionJournal.SetReleaseSuppressionActivityNotify(NotifyReleaseSuppressionActivity);
        InstrumentExecutionAuthority.SetReleaseSuppressionActivityNotify(NotifyReleaseSuppressionActivity);
    }

    /// <summary>
    /// Rebind stream + execution journals to <see cref="_persistenceBase"/> when SIM + <see cref="_ignoreExistingStreamJournals"/> (e.g. NT Playback account).
    /// Default SIM and live keep project root.
    /// </summary>
    private void RebindPersistenceIfNeeded(DateTimeOffset utcNow)
    {
        ApplyOptionalRunIdFromEnvironment();
        var nextBase = ComputeNextPersistenceBase(out var isolated);
        _isolatedPlaybackPersistence = isolated;
        try
        {
            var cur = Path.GetFullPath(_persistenceBase);
            var nxt = Path.GetFullPath(nextBase);
            if (string.Equals(cur, nxt, StringComparison.OrdinalIgnoreCase))
            {
                RebindRobotLogDirectoryIfNeeded();
                if (isolated)
                    RunRootArtifacts.EnsureBootstrapFiles(_persistenceBase, _root);
                return;
            }
        }
        catch
        {
            if (string.Equals(_persistenceBase, nextBase, StringComparison.OrdinalIgnoreCase))
            {
                RebindRobotLogDirectoryIfNeeded();
                if (isolated)
                    RunRootArtifacts.EnsureBootstrapFiles(_persistenceBase, _root);
                return;
            }
        }

        _persistenceBase = nextBase;
        try
        {
            Directory.CreateDirectory(RobotRunArtifactPaths.StateStreamJournals(_persistenceBase));
            Directory.CreateDirectory(RobotRunArtifactPaths.StateExecutionJournals(_persistenceBase));
            Directory.CreateDirectory(RobotRunArtifactPaths.DecisionsDir(_persistenceBase));
            Directory.CreateDirectory(Path.Combine(_persistenceBase, "events", "execution_events"));
            Directory.CreateDirectory(Path.Combine(_persistenceBase, "events", "ownership_events"));
            Directory.CreateDirectory(Path.Combine(_persistenceBase, "events", "ownership_snapshots"));
            Directory.CreateDirectory(Path.Combine(_persistenceBase, "events", "orphan_fills"));
            Directory.CreateDirectory(RobotRunArtifactPaths.LogsRobot(_persistenceBase));
            Directory.CreateDirectory(RobotRunArtifactPaths.LogsHealth(_persistenceBase));
            Directory.CreateDirectory(RobotRunArtifactPaths.LogsHydration(_persistenceBase));
            Directory.CreateDirectory(RobotRunArtifactPaths.LogsRanges(_persistenceBase));
            Directory.CreateDirectory(RobotRunArtifactPaths.LogsRangeBuilding(_persistenceBase));
            Directory.CreateDirectory(RobotRunArtifactPaths.DerivedExecutionSummaries(_persistenceBase));
            Directory.CreateDirectory(Path.Combine(_persistenceBase, "ops"));
        }
        catch (Exception ex)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "PERSISTENCE_REBIND_ERROR", state: "ENGINE",
                new { error = ex.Message, target = _persistenceBase }));
            throw;
        }

        if (isolated)
            RunRootArtifacts.EnsureBootstrapFiles(_persistenceBase, _root);

        RebindRobotLogDirectoryIfNeeded();

        _journals = new JournalStore(_persistenceBase);
        JournalParityPendingLedger.Clear();
        _executionJournal = new ExecutionJournal(_persistenceBase, _log);
        WireExecutionJournalReleaseSuppressionCallbacks();

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "PERSISTENCE_BASE_ACTIVE", state: "ENGINE",
            new
            {
                persistence_base = _persistenceBase,
                isolated_playback = isolated,
                run_id = _runId,
                note = isolated
                    ? "SIM + journal bypass (e.g. Playback account): stream and execution journals under runs/<run_id>/ (KEY_EVENTS.jsonl, summary.json on stop; see runs/LATEST_RUN.txt)."
                    : "Global persistence at project root."
            }));
    }

    private void ApplyIsolatedPlaybackAuditShadowDefaults(DateTimeOffset utcNow)
    {
        if (!_isolatedPlaybackPersistence) return;

        var canonicalWas = FeatureFlags.CanonicalOwnershipLedgerEnabled;
        var ueaShadowWas = FeatureFlags.UnifiedExecutionAuthorityShadowEnabled;
        var ueaActiveWas = FeatureFlags.UnifiedExecutionAuthorityEnabled;
        var repairWas = FeatureFlags.ReconciliationRepairExecutorEnabled;
        var structuralLedgerWas = FeatureFlags.StructuralLayerUseLedgerOwnership;

        FeatureFlags.CanonicalOwnershipLedgerEnabled = true;
        FeatureFlags.UnifiedExecutionAuthorityShadowEnabled = true;
        FeatureFlags.UnifiedExecutionAuthorityEnabled = true;
        FeatureFlags.ReconciliationRepairExecutorEnabled =
            repairWas || FeatureFlags.PlaybackAuditAutoEnableReconciliationRepairExecutor;
        FeatureFlags.StructuralLayerUseLedgerOwnership = true;

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "PLAYBACK_AUDIT_ACTIVE_FLAGS_APPLIED", state: "ENGINE",
            new
            {
                canonical_ownership_ledger_enabled = FeatureFlags.CanonicalOwnershipLedgerEnabled,
                unified_execution_authority_shadow_enabled = FeatureFlags.UnifiedExecutionAuthorityShadowEnabled,
                unified_execution_authority_enabled = FeatureFlags.UnifiedExecutionAuthorityEnabled,
                reconciliation_repair_executor_enabled = FeatureFlags.ReconciliationRepairExecutorEnabled,
                structural_layer_use_ledger_ownership = FeatureFlags.StructuralLayerUseLedgerOwnership,
                previous_canonical_ownership_ledger_enabled = canonicalWas,
                previous_unified_execution_authority_shadow_enabled = ueaShadowWas,
                previous_unified_execution_authority_enabled = ueaActiveWas,
                previous_reconciliation_repair_executor_enabled = repairWas,
                previous_structural_layer_use_ledger_ownership = structuralLedgerWas,
                playback_auto_enable_reconciliation_repair_executor = FeatureFlags.PlaybackAuditAutoEnableReconciliationRepairExecutor,
                note = "Isolated playback audit mode: canonical ledger, UEA active decisioning, and structural ledger ownership are enabled. Reconciliation repair execution is active only when explicitly enabled."
            }));
    }

    private void InitializeOwnershipAuditArtifacts(DateTimeOffset utcNow)
    {
        _stateEmitter?.Dispose();
        _stateEmitter = null;
        _ownershipLedger = null;
        _orphanFillJournal = null;
        _ownershipEventJournal = null;
        _reconciliationClassifier = null;
        _reconciliationRepairExecutor = null;

        if (FeatureFlags.CanonicalOwnershipLedgerEnabled)
        {
            _ownershipLedger = new InstrumentOwnershipLedger(
                _log,
                onClassAEvent: HandleOwnershipClassAEvent,
                onClassBEvent: HandleOwnershipClassBEvent);
            _orphanFillJournal = new OrphanFillJournal(_persistenceBase, _log, () => TradingDateString);
            _ownershipEventJournal = new OwnershipEventJournal(_persistenceBase, _log);
            _ownershipLedger.SetEventJournal(_ownershipEventJournal, TradingDateString);
            _stateEmitter = new AuthoritativeStateEmitter(
                _ownershipLedger,
                () => _executionAdapter?.GetAccountSnapshot(DateTimeOffset.UtcNow) ?? new AccountSnapshot(),
                () =>
                {
                    var instruments = new HashSet<string>(GetEngineScopedExecutionInstrumentKeys(), StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(_executionInstrument))
                        instruments.Add(_executionInstrument.Trim());
                    foreach (var s in _ownershipLedger.SnapshotAll(OwnershipAccountKey))
                    {
                        if (!string.IsNullOrWhiteSpace(s.ExecutionInstrumentKey))
                            instruments.Add(s.ExecutionInstrumentKey.Trim());
                    }
                    return instruments.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                },
                _persistenceBase,
                _log,
                account: OwnershipAccountKey,
                getSupervisoryState: inst => _mismatchCoordinator?.GetGateLifecyclePhase(inst).ToString(),
                getMismatchEscalationState: inst => _mismatchCoordinator?.GetBlockReason(inst),
                isInstrumentFrozen: inst => _frozenInstruments.Contains(inst),
                isKillSwitchActive: () => _killSwitch?.IsEnabled() ?? false,
                getAccountName: () => OwnershipAccountKey,
                getTradingDate: () => TradingDateString);
            _stateEmitter.StartPeriodicTimer();
            _reconciliationClassifier = new ReconciliationClassifier(_ownershipLedger, _log);
            if (FeatureFlags.ReconciliationRepairExecutorEnabled && _executionAdapter != null)
                _reconciliationRepairExecutor = new ReconciliationRepairExecutor(_executionJournal, _executionAdapter, _log);
        }

        _unifiedAuthority = (FeatureFlags.CanonicalOwnershipLedgerEnabled ||
                             FeatureFlags.UnifiedExecutionAuthorityShadowEnabled ||
                             FeatureFlags.UnifiedExecutionAuthorityEnabled)
            ? new UnifiedExecutionAuthority(_log, _ownershipLedger)
            : null;

        RunRootArtifacts.WriteAuditManifestJson(
            _persistenceBase,
            _runId,
            _engineStartUtc == default ? utcNow : _engineStartUtc,
            TradingDateString,
            _isolatedPlaybackPersistence,
            FeatureFlags.CanonicalOwnershipLedgerEnabled,
            FeatureFlags.UnifiedExecutionAuthorityShadowEnabled,
            FeatureFlags.UnifiedExecutionAuthorityEnabled,
            FeatureFlags.ReconciliationRepairExecutorEnabled,
            FeatureFlags.StructuralLayerUseLedgerOwnership);

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "AUDIT_ARTIFACT_PATHS_RESOLVED", state: "ENGINE",
            new
            {
                persistence_base = _persistenceBase,
                manifest = Path.Combine(_persistenceBase, RunRootArtifacts.AuditManifestFileName),
                robot_engine_log = Path.Combine(RobotRunArtifactPaths.LogsRobot(_persistenceBase), "robot_ENGINE.jsonl"),
                robot_instrument_glob = Path.Combine(RobotRunArtifactPaths.LogsRobot(_persistenceBase), "robot_*.jsonl"),
                ownership_events_dir = Path.Combine(_persistenceBase, "events", "ownership_events"),
                ownership_snapshots_dir = Path.Combine(_persistenceBase, "events", "ownership_snapshots"),
                orphan_fills_dir = Path.Combine(_persistenceBase, "events", "orphan_fills"),
                canonical_ownership_ledger_enabled = FeatureFlags.CanonicalOwnershipLedgerEnabled,
                unified_execution_authority_shadow_enabled = FeatureFlags.UnifiedExecutionAuthorityShadowEnabled,
                unified_execution_authority_enabled = FeatureFlags.UnifiedExecutionAuthorityEnabled,
                reconciliation_repair_executor_enabled = FeatureFlags.ReconciliationRepairExecutorEnabled,
                structural_layer_use_ledger_ownership = FeatureFlags.StructuralLayerUseLedgerOwnership
            }));
    }

    public void Start()
    {
        Volatile.Write(ref _shutdownRequested, 0);
        Volatile.Write(ref _shutdownCompleted, 0);
        _playbackStallQuiesceEligibleSinceUtc = DateTimeOffset.MinValue;
        _playbackStallQuiesceStopRequestedUtc = DateTimeOffset.MinValue;
        Volatile.Write(ref _playbackStallQuiesceArmed, 0);
        Volatile.Write(ref _playbackStallQuiesceStopRequested, 0);
        Volatile.Write(ref _playbackStallQuiesceForceFinalizeRequested, 0);
        Volatile.Write(ref _connectivityShutdownStopRequested, 0);
        Volatile.Write(ref _runWideShutdownStopRequested, 0);
        _robotBuildSignatureEmitted = false;
        if (_isolatedPlaybackPersistence)
            RunRootArtifacts.ClearRunShutdownSignal(_persistenceBase);
        // CRITICAL: Set run_id before any Start-path logs (RebindPersistenceIfNeeded → ApplyOptionalRunIdFromEnvironment refines from env after this)
        AssignRunIdAtSessionStart(out var runIdAssignmentSource);
        _engineStartUtc = _playbackStartTimeUtc ?? DateTimeOffset.UtcNow;
        RebindPersistenceIfNeeded(_engineStartUtc);
        ApplyIsolatedPlaybackAuditShadowDefaults(_engineStartUtc);
        InitializeOwnershipAuditArtifacts(_engineStartUtc);
        LogEvent(RobotEvents.EngineBase(_engineStartUtc, tradingDate: "", eventType: "RUN_ID_ASSIGNED", state: "ENGINE",
            new
            {
                run_id = _runId,
                source = runIdAssignmentSource,
                note = "Isolated playback: first engine sets QTSW2_RUN_ID when empty; subsequent engines reuse. Live: unique Guid per engine."
            }));
        try
        {
            Environment.SetEnvironmentVariable("QTSW2_ROBOT_PERSISTENCE_BASE", _persistenceBase);
        }
        catch
        {
            /* best-effort: static traces / tools resolve run root */
        }
        LogEvent(RobotEvents.EngineBase(_engineStartUtc, tradingDate: "", eventType: "PLAYBACK_JOURNAL_STARTUP", state: "ENGINE",
            new
            {
                PLAYBACK_DETECTED = _playbackAccountDetected,
                IGNORE_EXISTING_JOURNALS = _ignoreExistingStreamJournals,
                PLAYBACK_ANCHOR_UTC = _playbackStartTimeUtc.HasValue ? _playbackStartTimeUtc.Value.ToString("o") : null
            }));
        _eventWriter = new ExecutionEventWriter(_persistenceBase, () => TradingDateString, _log, () => _runId ?? "");
        _log.SetRunId(_runId);
        _runtimeAudit = new RuntimeAuditHub(_log, () => _runId ?? "");
        RuntimeAuditHubRef.Active = _runtimeAudit;
        _reconciliationConvergence = new ReconciliationConvergenceTracker(_log, () => _runId ?? "");

        var utcNow = _engineStartUtc;
        LogEngineBuildSignatureIfNeeded(utcNow, "ENGINE_START");

        // Initialize HealthMonitor after run_id is set so any health-monitor logs include run_id.
        InitializeHealthMonitorIfNeeded();
        _healthMonitor?.SetRunId(_runId);

        // Phase: initialize core under engine lock (serialize against timer/bar threads)
        lock (_engineLock)
        {
            // KillSwitch construction is logging-free; startup audit is emitted from LogInitialized() after run_id / logger rebind.
            if (_killSwitch == null)
            {
                _killSwitch = new KillSwitch(_root, _log);
                _killSwitch.LogInitialized();
            }

            // Start async logging service if enabled (Fix B)
            _loggingService?.Start();

            // Log resolved runtime paths/config for operator visibility
            var envProjectRoot = Environment.GetEnvironmentVariable("QTSW2_PROJECT_ROOT");
            var envLogDir = Environment.GetEnvironmentVariable("QTSW2_LOG_DIR");
            var cwd = "";
            try { cwd = Directory.GetCurrentDirectory(); } catch { cwd = ""; }

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "PROJECT_ROOT_RESOLVED", state: "ENGINE",
                new
                {
                    project_root = _root,
                    env_QTSW2_PROJECT_ROOT = envProjectRoot,
                    cwd,
                    spec_path = _specPath,
                    spec_exists = File.Exists(_specPath)
                }));

            var resolvedRobotLogDir = _activeRobotLogDir ?? "";
            string fpResolved;
            try { fpResolved = string.IsNullOrWhiteSpace(resolvedRobotLogDir) ? "" : Path.GetFullPath(resolvedRobotLogDir); }
            catch { fpResolved = resolvedRobotLogDir; }
            var ridScope = _runId?.Trim() ?? "";
            var isRunScoped = !string.IsNullOrEmpty(ridScope) && !string.IsNullOrEmpty(fpResolved) && fpResolved.IndexOf(ridScope, StringComparison.OrdinalIgnoreCase) >= 0;
            if (_isolatedPlaybackPersistence && !isRunScoped)
                throw new InvalidOperationException("LOG_DIR_RESOLVED: robot log directory must be run-scoped for isolated playback.");

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "LOG_DIR_RESOLVED", state: "ENGINE",
                new
                {
                    run_id = _runId,
                    is_run_scoped = isRunScoped,
                    log_dir = _activeRobotLogDir,
                    active_robot_log_dir = _activeRobotLogDir,
                    source = _resolvedLogDirSource,
                    env_QTSW2_LOG_DIR = envLogDir,
                    config_log_dir = _loggingConfig.log_dir,
                    warning = _resolvedLogDirWarning
                }));

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "LOGGING_CONFIG_LOADED", state: "ENGINE",
                new
                {
                    path = _loggingConfigPath,
                    exists = File.Exists(_loggingConfigPath),
                    max_file_size_mb = _loggingConfig.max_file_size_mb,
                    max_rotated_files = _loggingConfig.max_rotated_files,
                    min_log_level = _loggingConfig.min_log_level,
                    enable_diagnostic_logs = _loggingConfig.DiagnosticsEnabled,
                    diagnostic_rate_limits = _loggingConfig.diagnostic_rate_limits,
                    archive_days = _loggingConfig.archive_days
                }));

            // Startup dependency check: verify Robot.Contracts.dll is loadable (deployment integrity)
            try
            {
                var contractsType = Type.GetType("QTSW2.Robot.Contracts.IEventClock, Robot.Contracts");
                if (contractsType == null)
                    throw new InvalidOperationException("Robot.Contracts.IEventClock type not found");
            }
            catch (Exception ex)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "STARTUP_DEPENDENCY_CHECK_FAILED", state: "ENGINE",
                    new
                    {
                        assembly = "Robot.Contracts",
                        error = ex.Message,
                        exception_type = ex.GetType().Name,
                        fix_hint = "Deploy Robot.Contracts.dll to NinjaTrader bin directory; verify project reference and build output"
                    }));
            }

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENGINE_START", state: "ENGINE",
                new
                {
                    ninjatrader_instrument = _executionInstrument ?? "NULL",
                    note = "Instrument passed from NinjaTrader strategy constructor"
                }));

            try
            {
                _spec = ParitySpec.LoadFromFile(_specPath);
                // Debug log: confirm spec_name was loaded
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_NAME_LOADED", state: "ENGINE",
                    new { spec_name = _spec.spec_name }));
                _time = new TimeService(_spec.timezone);
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_LOADED", state: "ENGINE",
                    new { spec_name = _spec.spec_name, spec_revision = _spec.spec_revision, timezone = _spec.timezone }));

                // PHASE 4: Load execution policy (startup-only, not reloadable)
                try
                {
                    // Resolve absolute path for definitive logging
                    var absolutePolicyPath = Path.IsPathRooted(_executionPolicyPath)
                        ? _executionPolicyPath
                        : Path.Combine(_root, _executionPolicyPath);
                    absolutePolicyPath = Path.GetFullPath(absolutePolicyPath);

                    _executionPolicy = ExecutionPolicy.LoadFromFile(absolutePolicyPath);

                    // Compute file hash for audit trail
                    var policyFileHash = "";
                    try
                    {
                        var policyBytes = File.ReadAllBytes(absolutePolicyPath);
                        using (var sha256 = System.Security.Cryptography.SHA256.Create())
                        {
                            var hashBytes = sha256.ComputeHash(policyBytes);
                            policyFileHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but don't fail - hash is for audit only
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_POLICY_HASH_ERROR", state: "ENGINE",
                            new { error = ex.Message, note = "Failed to compute policy file hash (non-blocking)" }));
                    }

                    // Extract parsed values for diagnostic logging (raw JSON values, not derived)
                    var parsedValues = new Dictionary<string, object>();
                    foreach (var canonicalKvp in _executionPolicy.canonical_markets)
                    {
                        var canonicalMarket = canonicalKvp.Key;
                        var marketPolicy = canonicalKvp.Value;
                        var execInstruments = new Dictionary<string, object>();

                        foreach (var execKvp in marketPolicy.execution_instruments)
                        {
                            var execInst = execKvp.Key;
                            var instPolicy = execKvp.Value;
                            execInstruments[execInst] = new
                            {
                                enabled = instPolicy.enabled,
                                base_size = instPolicy.base_size,
                                max_size = instPolicy.max_size
                            };
                        }
                        parsedValues[canonicalMarket] = execInstruments;
                    }

                    // Log parsed values immediately after parsing (diagnostic)
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_POLICY_PARSED", state: "ENGINE",
                        new
                        {
                            parsed_values = parsedValues,
                            note = "Raw parsed JSON values immediately after deserialization"
                        }));

                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_POLICY_LOADED", state: "ENGINE",
                        new
                        {
                            file_path = absolutePolicyPath,
                            file_path_relative = _executionPolicyPath,
                            file_hash = policyFileHash,
                            file_size_bytes = new FileInfo(absolutePolicyPath).Length,
                            schema_id = _executionPolicy.schema,
                            note = "Execution policy loaded at startup; not reloadable"
                        }));

                    // 🔒 INVARIANT ENFORCEMENT: Log execution instrument anchor and policy validation at startup
                    // This provides proof of anchor and policy compliance for forensic clarity
                    if (!string.IsNullOrWhiteSpace(_executionInstrument))
                    {
                        var anchoredInstrument = _executionInstrument.ToUpperInvariant();
                        var anchoredCanonical = GetCanonicalInstrument(anchoredInstrument);
                        var policyValidated = false;
                        int? policyBaseSize = null;
                        int? policyMaxSize = null;

                        if (_executionPolicy != null)
                        {
                            var execInstPolicy = _executionPolicy.GetExecutionInstrumentPolicy(anchoredCanonical, anchoredInstrument);
                            if (execInstPolicy != null && execInstPolicy.enabled)
                            {
                                policyValidated = true;
                                policyBaseSize = execInstPolicy.base_size;
                                policyMaxSize = execInstPolicy.max_size;
                            }
                        }

                        var note = policyValidated
                            ? $"Strategy anchored to execution instrument: {anchoredInstrument} (policy validated)"
                            : $"Strategy anchored to execution instrument: {anchoredInstrument} (policy validation failed)";

                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_INSTRUMENT_ANCHORED", state: "ENGINE",
                            new
                            {
                                execution_instrument = anchoredInstrument,
                                canonical_instrument = anchoredCanonical,
                                policy_validated = policyValidated,
                                policy_base_size = policyBaseSize,
                                policy_max_size = policyMaxSize,
                                note = note
                            }));
                    }
                }
                catch (FileNotFoundException ex)
                {
                    var errorMsg = $"PHASE 4: Execution policy file not found: {_executionPolicyPath}. Execution blocked.";
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_POLICY_VALIDATION_FAILED", state: "ENGINE",
                        new
                        {
                            error = ex.Message,
                            file_path = _executionPolicyPath,
                            note = "Execution policy file is required. Execution blocked."
                        }));

                    // Centralized notification
                    ReportExecutionPolicyFailure(
                        summary: "Execution policy file not found",
                        details: new List<string> { ex.Message },
                        context: new Dictionary<string, object> { ["exception_type"] = ex.GetType().Name }
                    );

                    throw new InvalidOperationException(errorMsg, ex);
                }
                catch (InvalidOperationException ex)
                {
                    // Policy validation failed
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_POLICY_VALIDATION_FAILED", state: "ENGINE",
                        new
                        {
                            error = ex.Message,
                            file_path = _executionPolicyPath,
                            note = "Execution policy validation failed. Execution blocked."
                        }));

                    // Centralized notification
                    ReportExecutionPolicyFailure(
                        summary: "Execution policy validation failed",
                        details: new List<string> { ex.Message }
                    );

                    throw;
                }
                catch (Exception ex)
                {
                    var errorMsg = $"PHASE 4: Failed to load execution policy: {ex.Message}";
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_POLICY_VALIDATION_FAILED", state: "ENGINE",
                        new
                        {
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            file_path = _executionPolicyPath,
                            note = "Execution policy load/parse failed. Execution blocked."
                        }));

                    // Centralized notification
                    ReportExecutionPolicyFailure(
                        summary: "Execution policy load/parse failed",
                        details: new List<string> { ex.Message },
                        context: new Dictionary<string, object> { ["exception_type"] = ex.GetType().Name }
                    );

                    throw new InvalidOperationException(errorMsg, ex);
                }

                // PHASE 4: Policy validation occurs in ApplyTimetable based on actual enabled directives
                // No single-instrument assumption validation here - enforcement happens where streams are created

                // PHASE 4: Emit policy activation log (canonical market lock removed - multiple instances per market allowed)
                if (!string.IsNullOrWhiteSpace(_executionInstrument) && _executionPolicy != null)
                {
                    var canonicalInstrument = GetCanonicalInstrument(_executionInstrument);
                    var execInstPolicy = _executionPolicy.GetExecutionInstrumentPolicy(canonicalInstrument, _executionInstrument);
                    if (execInstPolicy != null && execInstPolicy.enabled)
                    {
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_POLICY_ACTIVE", state: "ENGINE",
                            new
                            {
                                canonical_instrument = canonicalInstrument,
                                execution_instrument = _executionInstrument,
                                resolved_order_quantity = execInstPolicy.base_size,
                                base_size = execInstPolicy.base_size,
                                max_size = execInstPolicy.max_size,
                                note = "Policy loaded at startup; not reloadable"
                            }));
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_INVALID", state: "ENGINE",
                    new { error = ex.Message }));
                throw;
            }

            // Initialize execution components now that spec is loaded
            if (_killSwitch == null)
                throw new InvalidOperationException("KillSwitch must be initialized before RiskGate.");
            // Hydrate risk latches (persisted instrument blocks) so blocks survive restarts
            _riskLatchManager = new RiskLatchManager(_persistenceBase, _accountName ?? "UNKNOWN");
            foreach (var inst in _riskLatchManager.Hydrate())
            {
                _frozenInstruments.Add(inst);
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "RISK_LATCH_HYDRATED", state: "ENGINE",
                    new { instrument = inst, note = "Persisted block re-applied on startup" }));
            }
            _riskGate = new RiskGate(_spec, _time, _log, _killSwitch, guard: this, isInstrumentFrozen: IsInstrumentFrozenOrSupervisorilyBlocked);
            _riskGate.SetOnGlobalKillSwitchBlocked((eventType, instrument, stream) =>
            {
                _healthMonitor?.ReportCritical(eventType, new Dictionary<string, object> { ["instrument"] = instrument, ["stream"] = stream, ["note"] = "Global kill switch blocks all execution" });
            });

            // Create adapter (LIVE uses NinjaTraderLiveAdapter; Strategy wires SetNTContext)
            _executionAdapter = ExecutionAdapterFactory.Create(_executionMode, _root, _log, _executionJournal, _time, persistenceRoot: _persistenceBase);

            // Set journal corruption callback for fail-closed behavior
            _executionJournal.SetJournalCorruptionCallback((stream, tradingDate, intentId, utcNow) =>
            {
                StandDownStream(stream, utcNow, $"JOURNAL_CORRUPTION:{intentId}");
            });

            // Set execution cost callback for cost tracking
            _executionJournal.SetExecutionCostCallback((intentId, slippageDollars, commission, fees) =>
            {
                _executionSummary.RecordExecutionCost(intentId, slippageDollars, commission, fees);
            });
            _executionJournal.SetTaggedBrokerJournalRehydrationCallback(
                (iid, td, stream, execInst, dir, eQty, xQty, utc) =>
                    TryRehydrateSingleIntentExposureFromDurableJournal(iid, td, stream, execInst, dir, eQty, xQty, utc,
                        "tagged_broker_recovery"));
            _executionJournal.SetTradeCompletedCallback((intentId, tradingDate, stream, completedUtc, completionReason) =>
            {
                foreach (var s in _streams.Values)
                    s.HandleExecutionTradeCompleted(intentId, completedUtc, completionReason);
            });

            // Create intent exposure coordinator
            var coordinator = new InstrumentIntentCoordinator(
                _log,
                () => _executionAdapter.GetAccountSnapshot(DateTimeOffset.UtcNow),
                (streamId, now, reason) => StandDownStream(streamId, now, reason),
                (intentId, instrument, now) => FlattenIntent(intentId, instrument, now),
                (intentId, now) => CancelIntentOrders(intentId, now));
            _intentExposureCoordinator = coordinator;

            // PHASE 2: Set engine callbacks for protective order failure recovery
            if (_executionAdapter is NinjaTraderSimAdapter simAdapter)
            {
                simAdapter.SetUseInstrumentExecutionAuthority(_executionPolicy?.UseInstrumentExecutionAuthority ?? false);
                simAdapter.SetAggregationPolicy(_executionPolicy?.Aggregation);
                simAdapter.SetEventWriter(_eventWriter);
                simAdapter.SetFlattenCoordinationInstanceId(_reconciliationWriterInstanceId);
                simAdapter.SetCanonicalInstrumentForJournalAggregation(s => GetCanonicalInstrument(s));
                simAdapter.SetEngineCallbacks(
                    standDownStreamCallback: (streamId, now, reason) => StandDownStream(streamId, now, reason),
                    getNotificationServiceCallback: () => GetNotificationService(),
                    isExecutionAllowedCallback: () => IsExecutionAllowed(),
                    onReentryFillCallback: (reentryIntentId, now) =>
                    {
                        foreach (var s in _streams.Values)
                            s.HandleReentryFill(reentryIntentId, now);
                    },
                    onReentryProtectionAcceptedCallback: (reentryIntentId, now) =>
                    {
                        foreach (var s in _streams.Values)
                            s.HandleReentryProtectionAccepted(now, reentryIntentId);
                    },
                    onReentrySubmitCompletedCallback: (reentryIntentId, now, success, error) =>
                    {
                        foreach (var s in _streams.Values)
                            s.HandleReentrySubmitCompleted(reentryIntentId, now, success, error);
                    },
                    blockInstrumentCallback: (instrument, now, reason) =>
                    {
                        _executionAdapter?.RequestSupervisoryActionForInstrument(instrument, SupervisoryTriggerReason.IEA_ENQUEUE_FAILURE, SupervisorySeverity.HIGH, new { reason }, now);
                        if (_executionAdapter?.TryEnqueueEmergencyFlattenProtective(instrument, now) != true)
                        {
                            LogEvent(RobotEvents.EngineBase(now, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "",
                                eventType: "FLATTEN_EMERGENCY_ON_BLOCK_ENQUEUE_UNSUPPORTED", state: "ENGINE",
                                new { instrument, reason, note = "Emergency flatten enqueue not supported for adapter; position may be unprotected until manual action" }));
                        }
                        StandDownStreamsForInstrument(instrument, now, reason);
                        _healthMonitor?.ReportCritical("IEA_ENQUEUE_FAILURE_INSTRUMENT_BLOCKED", new Dictionary<string, object>
                        {
                            { "instrument", instrument },
                            { "reason", reason },
                            { "policy", "IEA_FAIL_CLOSED_BLOCK_INSTRUMENT" },
                            { "note", "IEA queue timeout or overflow — instrument blocked; no new intents until restart" }
                        });
                    },
                    onSupervisoryCriticalCallback: (eventType, instrument, payload) =>
                    {
                        var dict = payload as Dictionary<string, object>;
                        if (dict == null && payload != null)
                        {
                            dict = new Dictionary<string, object> { ["instrument"] = instrument, ["reason"] = payload.ToString() ?? "" };
                        }
                        else if (dict == null)
                            dict = new Dictionary<string, object> { ["instrument"] = instrument };
                        _healthMonitor?.ReportCritical(eventType, dict);
                    },
                    shouldCancelEntryOrdersForStreamCallback: (streamId, tradingDate) =>
                    {
                        if (!_streams.TryGetValue(streamId, out var stream))
                            return (true, "stream_not_found");
                        if (stream.TradingDate != tradingDate)
                            return (true, "stream_trading_date_mismatch");
                        var (eligible, reason) = stream.GetEntryOrderCancellationEligibility();
                        return (!eligible, reason);
                    },
                    hasSlotJournalWithEntryStopsForInstrumentCallback: HasSlotJournalWithEntryStopsForInstrument, // Bootstrap: ADOPT entry stops on restart
                    getActiveTradingDateString: () => TradingDateString,
                    isGlobalKillSwitchActive: () => _killSwitch?.IsEnabled() == true,
                    isMismatchExecutionBlocked: inst =>
                        _mismatchCoordinator != null && _mismatchCoordinator.IsInstrumentBlockedByMismatch(inst),
                    isMismatchExecutionBlockedForSubmit: (inst, submitPath) =>
                        _mismatchCoordinator != null && _mismatchCoordinator.IsSubmitBlockedByMismatch(inst, submitPath),
                    isInstrumentFrozenOrEpaBlocked: IsInstrumentEpaAdapterSubmitBlocked,
                    isPlaybackStallNtCallBlocked: () => IsPlaybackStallQuiescenceBlockingNtCalls());

                // Wire coordinator to adapter
                simAdapter.SetCoordinator(coordinator);
            }

            // Create reconciliation runner for orphaned journal cleanup + quantity reconciliation
            _reconciliationRunner = new ReconciliationRunner(_executionAdapter, _executionJournal, _log,
                onQuantityMismatch: LogReconciliationQuantityMismatchDiagnostics,
                onReconciliationPassComplete: qtyByInstrument =>
                {
                    _lastRunnerQtyByInstrument = qtyByInstrument;
                    var resolvedUtc = DateTimeOffset.UtcNow;
                    foreach (var kv in qtyByInstrument)
                    {
                        var rInst = kv.Key;
                        var (raq, rjq) = kv.Value;
                        if (ReconciliationStateTracker.Shared.TryMarkResolved(_accountName, rInst, raq, rjq, resolvedUtc,
                                out var prevOwner, out var prevRecoveryState))
                        {
                            LogEvent(RobotEvents.EngineBase(resolvedUtc, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "",
                                eventType: "RECONCILIATION_RESOLVED", state: "ENGINE",
                                new
                                {
                                    account = _accountName ?? "",
                                    instrument = rInst,
                                    account_qty = raq,
                                    journal_qty = rjq,
                                    owner_instance_id = prevOwner,
                                    previous_recovery_state = prevRecoveryState.ToString(),
                                    metrics = new
                                    {
                                        reconciliation_mismatch_total = ReconciliationStateTracker.Shared.Metrics.ReconciliationMismatchTotal,
                                        reconciliation_debounced_total = ReconciliationStateTracker.Shared.Metrics.ReconciliationDebouncedTotal,
                                        reconciliation_secondary_skipped_total = ReconciliationStateTracker.Shared.Metrics.ReconciliationSecondarySkippedTotal,
                                        reconciliation_resolved_total = ReconciliationStateTracker.Shared.Metrics.ReconciliationResolvedTotal
                                    },
                                    note = "Qty match — reconciliation episode cleared"
                                }));
                        }
                    }

                    // Intentionally no unfreeze here: reconciliation must not mutate execution latch state.
                },
                reconciliationAccountName: () => _accountName,
                reconciliationInstanceId: () => _reconciliationWriterInstanceId,
                reconciliationTracker: ReconciliationStateTracker.Shared,
                reconciliationDebounceWindow: null,
                runtimeAudit: _runtimeAudit,
                convergenceTracker: _reconciliationConvergence,
                redundancySuppression: _releaseReconRedundancy,
                getCanonicalInstrumentForJournalAggregation: s => GetCanonicalInstrument(s),
                getPendingExecutionWorkloadForInstrument: GetPendingIeAWorkloadForBrokerInstrument,
                isInstrumentRecoveryRelevant: IsExecutionInstrumentInLiveRecoveryScope,
                getRunIdForReconciliationDiagnostics: () => _runId,
                ownershipLedger: _ownershipLedger,
                getOwnershipAccountKey: () => OwnershipAccountKey);

            // Gap 3 Phase 3–5: Protective coverage coordinator (blocks, corrective, emergency flatten escalation)
            _protectiveCoordinator = new ProtectiveCoverageCoordinator(
                getSnapshot: () => _executionAdapter.GetAccountSnapshot(DateTimeOffset.UtcNow),
                getActiveInstruments: () => GetEngineScopedExecutionInstrumentKeys(),
                isFlattenInProgress: _ => false,
                isRecoveryInProgress: _ => false,
                isInstrumentBlocked: inst => _frozenInstruments.Contains(inst),
                log: _log,
                submitCorrective: TrySubmitCorrectiveStop,
                emergencyFlatten: (instrument, utcNow) =>
                {
                    if (_executionAdapter?.TryEnqueueEmergencyFlattenProtective(instrument, utcNow) == true)
                        return FlattenResult.FailureResult("Enqueued for strategy thread", utcNow);
                    return FlattenResult.FailureResult("Emergency flatten enqueue not supported for this adapter", utcNow);
                },
                eventWriter: _eventWriter,
                runtimeAudit: _runtimeAudit,
                isInstrumentInEngineScope: IsExecutionInstrumentInThisEngineScope,
                getPendingExecutionWorkloadForInstrument: GetPendingIeAWorkloadForBrokerInstrument,
                getRunIdForMismatchDiagnostics: () => _runId);

            // Gap 4 + P1.5: Mismatch escalation + closed-loop state-consistency gate
            var stableWindowMs = _executionMode == ExecutionMode.SIM
                ? MismatchEscalationPolicy.STATE_CONSISTENCY_STABLE_WINDOW_MS_SIM
                : MismatchEscalationPolicy.STATE_CONSISTENCY_STABLE_WINDOW_MS_LIVE;
            _mismatchCoordinator = new MismatchEscalationCoordinator(
                getSnapshot: () => _executionAdapter.GetAccountSnapshot(DateTimeOffset.UtcNow),
                getActiveInstruments: () => GetEngineScopedExecutionInstrumentKeys(),
                getMismatchObservations: (snap, utcNow) => AssembleMismatchObservations(snap, utcNow),
                isInstrumentBlocked: inst => _frozenInstruments.Contains(inst) || (_protectiveCoordinator?.IsInstrumentBlockedByProtective(inst) ?? false),
                isFlattenInProgress: _ => false,
                isRecoveryInProgress: _ => false,
                log: _log,
                eventWriter: _eventWriter,
                runInstrumentGateReconciliation: (inst, utc, cycle) => RunInstrumentGateReconciliation(inst, utc, cycle),
                evaluateReleaseReadiness: (inst, snap, utc, forceFull) =>
                    EvaluateStateConsistencyReleaseReadiness(inst, snap, utc, forceFull),
                stateConsistencyStableWindowMs: stableWindowMs,
                runtimeAudit: _runtimeAudit,
                runForcedBrokerAlignment: RunForcedBrokerAlignment,
                onForcedConvergenceFailure: OnForcedBrokerConvergenceFailure,
                getExecutionActivityGeneration: () => _releaseReconRedundancy.ExecutionActivityGeneration,
                onHedgedNetFlatPersistentEscalation: (inst, utc) =>
                {
                    _reconciliationRunner?.ForceRunGateRecoveryForInstrument(utc, inst);
                    LogEvent(RobotEvents.EngineBase(utc, tradingDate: TradingDateString,
                        eventType: "HEDGED_NET_FLAT_GROSS_OPEN_CONVERGENCE_HOOK", state: "ENGINE",
                        new
                        {
                            instrument = inst,
                            note = "Persistent hedged net-flat gross-open — gate recovery scheduled (broker truth; journal model)"
                        }));
                },
                isInstrumentInEngineScope: IsExecutionInstrumentInThisEngineScope,
                isInstrumentRecoveryRelevant: IsExecutionInstrumentInLiveRecoveryScope,
                getPendingExecutionWorkloadForInstrument: GetPendingIeAWorkloadForBrokerInstrument,
                getRunIdForMismatchDiagnostics: () => _runId,
                probeCanonicallyUnexplainedExposure: (inst, utc) => ProbeMismatchConvergenceCanonicalExposure(inst, utc));

            // Phase 8b: wire ledger-aware mismatch detection when enabled
            if (FeatureFlags.CanonicalOwnershipLedgerEnabled && _ownershipLedger != null)
            {
                _mismatchCoordinator.SetLedgerSnapshotProvider(
                    inst => _ownershipLedger.GetOwnershipSnapshot(OwnershipAccountKey, inst));
            }

            if (_executionAdapter is NinjaTraderSimAdapter simP2)
            {
                simP2.SetPendingFillBridge(_pendingFillBridge);
                if (FeatureFlags.CanonicalOwnershipLedgerEnabled)
                {
                    simP2.SetOwnershipLedger(_ownershipLedger);
                    simP2.SetOrphanFillJournal(_orphanFillJournal);
                }
                simP2.SetUnifiedExecutionAuthority(_unifiedAuthority);
                simP2.SetMismatchExecutionTriggerCallback((inst, utc, d) =>
                {
                    _releaseReconRedundancy.NotifyExecutionActivity();
                    _mismatchCoordinator?.NotifyExecutionTrigger(inst, utc, d);
                    _mismatchCoordinator?.NotifyReconciliationAuditWake();
                    TryEnsureJournalIntegrityAfterExecutionActivity(inst, utc, d);
                });
                simP2.SetInstrumentMismatchGateEngagedCallback(inst =>
                    _mismatchCoordinator != null && _mismatchCoordinator.IsInstrumentBlockedByMismatch(inst));
                simP2.SetP2StreamContainmentEngineCallback((attr, now) =>
                {
                    foreach (var streamId in attr.ImplicatedStreams)
                    {
                        if (!string.IsNullOrEmpty(streamId))
                            StandDownSingleStreamForOwnershipAmbiguity(streamId, now, attr);
                    }
                });
            }

            // Log execution mode and adapter
            var adapterType = _executionAdapter.GetType().Name;
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_MODE_SET", state: "ENGINE",
                new { mode = _executionMode.ToString(), adapter = adapterType }));
        }

        // Timetable disk I/O happens outside the engine lock; application happens under the lock.
        var parsed = PollAndParseTimetable(utcNow);

        lock (_engineLock)
        {
            // Load timetable and lock trading date from it (fail closed if invalid)
            // Trading date is locked immediately from timetable, then streams are created
            ReloadTimetableIfChanged(utcNow, force: true, parsed.Poll, parsed.Timetable, parsed.ParseException);

            // If trading date was locked from timetable, create streams and emit banner
            if (_activeTradingDate.HasValue)
            {
                EnsureStreamsCreated(utcNow);
                EmitStartupBanner(utcNow);
                RehydrateOpenIntentExposuresFromJournal(utcNow);
            }
            // Otherwise, timetable was invalid or missing trading_date - StandDown() was called

            // Initialize heartbeat timestamp
            _lastTickUtc = utcNow;

            // Start health monitor if enabled
            _healthMonitor?.Start();
        }
    }


    /// <summary>Tick with explicit Historical flag. Use this when caller knows State (e.g. strategy).</summary>
    public void Tick(DateTimeOffset utcNow, bool isHistorical)
    {
        TickInternal(utcNow, isHistorical);
    }

    /// <summary>Tick with inferred Historical (bar time lag). Prefer Tick(utcNow, isHistorical) when caller has State.</summary>
    public void Tick(DateTimeOffset utcNow)
    {
        var nowWall = DateTimeOffset.UtcNow;
        var barTimeLagMinutes = (nowWall - utcNow).TotalMinutes;
        var inferredHistorical = barTimeLagMinutes > 2.0;
        TickInternal(utcNow, inferredHistorical);
    }

    private void TickInternal(DateTimeOffset utcNow, bool isHistorical)
    {
        if (IsTerminalShutdownLatched()) return;
        if (TryRespectRunWideShutdownSignal(utcNow, "tick_entry"))
            return;
        var tickTotalStart = _runtimeAudit != null ? RuntimeAuditHub.CpuStart() : 0L;
        try
        {
        var cpuProf = EngineCpuProfile.IsEnabled();
        Stopwatch? swPreLock = cpuProf ? Stopwatch.StartNew() : null;
        // PHASE 3.1: Periodic identity invariants check (rate-limited). Skip during Historical - Realtime-only.
        if (!isHistorical)
            CheckIdentityInvariantsIfNeeded(utcNow);

        // Check for test notification trigger file (outside lock for performance)
        var triggerFile = Path.Combine(_root, "data", "test_notification_trigger.txt");
        if (File.Exists(triggerFile))
        {
            try
            {
                File.Delete(triggerFile);
                SendTestNotification();
            }
            catch (Exception ex)
            {
                // Log but don't throw - tick must never crash
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TEST_NOTIFICATION_TRIGGER_ERROR", state: "ENGINE",
                    new { error = ex.Message }));
            }
        }

        // CRITICAL: During Historical, skip timetable poll entirely - we're replaying bars, no config changes.
        // This avoids ~400 file reads + SHA256 + JSON parse per strategy during Historical.
        var nowWall = DateTimeOffset.UtcNow;
        var shouldPoll = !isHistorical && _timetablePoller.ShouldPoll(nowWall);
        var parsed = shouldPoll ? PollAndParseTimetable(nowWall) : default;

        var preLockMs = swPreLock?.ElapsedMilliseconds ?? 0;

        // Diagnostic: log once per engine when we skip poll during Historical (confirms fix is deployed)
        if (isHistorical && !_loggedHistoricalPollSkip)
        {
            _loggedHistoricalPollSkip = true;
            LogEvent(RobotEvents.EngineBase(nowWall, tradingDate: TradingDateString, eventType: "HISTORICAL_POLL_SKIP_ACTIVE", state: "ENGINE",
                new { note = "Timetable poll skipped during Historical - fix deployed" }));
        }

        lock (_engineLock)
        {
            var lockRegionStart = _runtimeAudit != null ? RuntimeAuditHub.CpuStart() : 0L;
            try
            {
            Stopwatch? wPreRecon = cpuProf ? Stopwatch.StartNew() : null;
            long preReconMs = 0;
            long reconciliationRunnerMs = 0;
            long timetableReloadMs = 0;
            long streamTickMs = 0;
            long secondReconciliationMs = 0;
            long forcedFlattenMs = 0;
            long tailCoordinatorsMs = 0;

            // HEARTBEAT: Emit unconditionally for process liveness (before any early returns)
            // This ensures watchdog always sees engine liveness, regardless of trading readiness

            // WATCHDOG: Log ENGINE_TICK_CALLSITE for watchdog liveness monitoring
            // Rate-limited to once per 5 seconds of REAL time, not bar time.
            // During Historical, utcNow is bar time (1 min/bar) so (utcNow - last) would fire every bar.
            // Use wall-clock (nowWall from outer scope) for rate limit so we log at most every 5s.
            var shouldLogTickCallsite = !_lastEngineTickCallsiteLogUtc.HasValue ||
                (nowWall - _lastEngineTickCallsiteLogUtc.Value).TotalSeconds >= ENGINE_TICK_CALLSITE_RATE_LIMIT_SECONDS;
            if (shouldLogTickCallsite)
            {
                _lastEngineTickCallsiteLogUtc = nowWall;
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ENGINE_TICK_CALLSITE", state: "ENGINE",
                    new
                    {
                        note = "Tick() called - watchdog liveness signal. Rate-limited to every 5 seconds."
                    }));
            }

            // PHASE 3: Update engine heartbeat timestamp for liveness monitoring
            _lastTickUtc = utcNow;
            _lastEngineTickUtc = utcNow; // Also update for broker sync gate

            // PHASE 3: Update health monitor with engine tick timestamp
            _healthMonitor?.UpdateEngineTick(utcNow, nowWall);


            // TRADING READINESS: Guards that prevent trading logic from running when engine is not ready
            // CRITICAL: Defensive checks - engine must be initialized for trading logic
            if (_spec is null)
            {
                // Spec should be loaded in Start() - if null, engine is in invalid state
                // Log error but don't throw (to prevent tick timer from crashing)
                // Heartbeat already emitted above, so watchdog will see engine is alive
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENGINE_TICK_INVALID_STATE", state: "ENGINE",
                    new { error = "Spec is null - engine not properly initialized" }));
                return;
            }

            if (_time is null)
            {
                // TimeService should be created in Start() - if null, engine is in invalid state
                // Heartbeat already emitted above, so watchdog will see engine is alive
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENGINE_TICK_INVALID_STATE", state: "ENGINE",
                    new { error = "TimeService is null - engine not properly initialized" }));
                return;
            }

            // Broker sync gate: Check if we're waiting for synchronization
            if (_recoveryState == ConnectionRecoveryState.RECONNECTED_RECOVERY_PENDING)
            {
                if (!IsBrokerSynchronized(utcNow))
                {
                    // Rate-limited log: emit at most once every 5 seconds
                    var shouldLog = !_lastSyncWaitLogUtc.HasValue ||
                                    (utcNow - _lastSyncWaitLogUtc.Value).TotalSeconds >= 5.0;

                    if (shouldLog)
                    {
                        _lastSyncWaitLogUtc = utcNow;
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_RECOVERY_WAITING_FOR_SYNC", state: "ENGINE",
                            new
                            {
                                recovery_state = _recoveryState.ToString(),
                                reconnect_utc = _reconnectUtc?.ToString("o"),
                                last_tick_utc = _lastTickUtc != DateTimeOffset.MinValue ? _lastTickUtc.ToString("o") : null,
                                last_order_update_utc = _lastOrderUpdateUtc?.ToString("o"),
                                last_execution_update_utc = _lastExecutionUpdateUtc?.ToString("o"),
                                last_connection_status = _lastConnectionStatus.ToString(),
                                quiet_window_seconds = 5,
                                note = "Waiting for broker synchronization (bar/order/execution updates) before starting recovery"
                            }));
                    }
                    return; // Don't proceed with normal tick processing while waiting
                }

                // Broker is synchronized: transition to RECOVERY_RUNNING and start recovery
                _recoveryState = ConnectionRecoveryState.RECOVERY_RUNNING;
                _secondReconciliationRunUtc = null; // Reset for new recovery cycle
                if (!_recoveryStartedUtc.HasValue)
                {
                    _recoveryStartedUtc = utcNow;
                }

                // Start recovery runner (idempotent, single-threaded)
                RunRecovery(utcNow, _accountName ?? "");
            }

            preReconMs = wPreRecon?.ElapsedMilliseconds ?? 0;
            Stopwatch? wRecon = cpuProf ? Stopwatch.StartNew() : null;

            // Reconciliation: periodic orphaned journal cleanup (throttled). Skip during Historical - no live account.
            // Drain non-IEA callback ingress first so journal updates from executions are applied before reconciliation.
            if (!isHistorical)
            {
                LogAndDrainCallbackIngressBeforeReconciliation(utcNow, nowWall);
                RunReconciliationPeriodicThrottle(utcNow);
                if (IsTerminalShutdownLatched() || TryRespectRunWideShutdownSignal(utcNow, "post_reconciliation_throttle"))
                    return;
            }

            reconciliationRunnerMs = wRecon?.ElapsedMilliseconds ?? 0;
            Stopwatch? wTimetableStream = cpuProf ? Stopwatch.StartNew() : null;

            // Timetable reactivity (disk I/O already completed outside lock)
            if (shouldPoll)
            {
                // PHASE 3: Update health monitor with timetable poll timestamp
                _healthMonitor?.UpdateTimetablePoll(utcNow);

                ReloadTimetableIfChanged(utcNow, force: false, parsed.Poll, parsed.Timetable, parsed.ParseException);
            }

            timetableReloadMs = shouldPoll ? (wTimetableStream?.ElapsedMilliseconds ?? 0) : 0;
            if (cpuProf && wTimetableStream != null)
                wTimetableStream.Restart();

            // SLOT EXPIRY: Run stream.Tick BEFORE forced flatten so slot expiry (at NextSlotTimeUtc)
            // can run and commit streams before forced flatten. Otherwise forced flatten would
            // commit pre-entry streams first, and reentry streams would never reach slot expiry.
            var streamLoopStart = _runtimeAudit != null ? RuntimeAuditHub.CpuStart() : 0L;
            foreach (var s in _streams.Values)
            {
                var ts = _runtimeAudit != null ? RuntimeAuditHub.CpuStart() : 0L;
                s.Tick(utcNow);
                if (ts != 0)
                {
                    _runtimeAudit?.CpuEnd(ts, RuntimeAuditSubsystem.EngineTickPerStream, s.Instrument, s.Stream, onIeaWorker: false);
                    var sid = string.IsNullOrEmpty(s.Stream) ? s.Instrument : s.Stream;
                    _runtimeAudit?.RecordStreamTick(sid, RuntimeAuditHub.CpuElapsedMs(ts));
                }
            }
            if (streamLoopStart != 0)
            {
                var loopMs = RuntimeAuditHub.CpuElapsedMs(streamLoopStart);
                _runtimeAudit?.CpuEnd(streamLoopStart, RuntimeAuditSubsystem.StreamLoop);
                _runtimeAudit?.RecordStreamLoopAggregate(_streams.Count, loopMs);
            }
            if (IsTerminalShutdownLatched() || TryRespectRunWideShutdownSignal(utcNow, "post_stream_tick"))
                return;

            streamTickMs = wTimetableStream?.ElapsedMilliseconds ?? 0;
            if (cpuProf && wTimetableStream != null)
                wTimetableStream.Restart();

            Stopwatch? wSecondRecon = null;
            // Second reconciliation trigger (lightweight safety net): run once, ~5 min after recovery
            if (_recoveryState == ConnectionRecoveryState.RECOVERY_COMPLETE &&
                _recoveryCompletedUtc.HasValue &&
                !_secondReconciliationRunUtc.HasValue &&
                _executionAdapter != null &&
                (utcNow - _recoveryCompletedUtc.Value).TotalMinutes >= SECOND_RECONCILIATION_DELAY_MINUTES)
            {
                if (cpuProf)
                    wSecondRecon = Stopwatch.StartNew();
                try
                {
                    var snap = _executionAdapter.GetAccountSnapshot(utcNow);
                    var streamsReconciled = 0;
                    var streamsNeedingResubmit = 0;
                    foreach (var stream in _streams.Values)
                    {
                        if (stream.Committed || stream.State != StreamState.RANGE_LOCKED) continue;
                        var (reconciled, needsResubmit) = stream.ReconcileEntryOrders(snap, utcNow);
                        if (reconciled) streamsReconciled++;
                        if (needsResubmit) streamsNeedingResubmit++;
                    }
                    _secondReconciliationRunUtc = utcNow;
                    if (streamsReconciled > 0 || streamsNeedingResubmit > 0)
                    {
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_SECOND_RECONCILIATION", state: "ENGINE",
                            new
                            {
                                streams_reconciled = streamsReconciled,
                                streams_needing_resubmit = streamsNeedingResubmit,
                                delay_minutes = SECOND_RECONCILIATION_DELAY_MINUTES,
                                note = "Lightweight second reconciliation (safety net)"
                            }));
                    }
                }
                catch (Exception ex)
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_SECOND_RECONCILIATION_ERROR", state: "ENGINE",
                        new { error = ex.Message, note = "Second reconciliation failed - non-fatal" }));
                    _secondReconciliationRunUtc = utcNow; // Don't retry
                }
            }

            secondReconciliationMs = wSecondRecon?.ElapsedMilliseconds ?? 0;
            if (cpuProf && wTimetableStream != null)
                wTimetableStream.Restart();

            Stopwatch? wForcedFlatten = cpuProf ? Stopwatch.StartNew() : null;

            // Forced flatten block — per sessionClass (runs after slot expiry so both can work)
            var tradingDateStr = TradingDateString;
            if (!string.IsNullOrEmpty(tradingDateStr))
            {
                foreach (var sessionClass in new[] { "S1", "S2" })
                {
                    var sessionTruth = ResolveSessionTruth(tradingDateStr, sessionClass, utcNow);
                    if (!sessionTruth.IsResolved || sessionTruth.Result == null)
                    {
                        // No cache and fallback failed — log skip and SESSION_CLOSE_CACHE_MISSING if persistent
                        var shouldLogSkip = !_lastForcedFlattenSkipLogUtc.HasValue ||
                            (utcNow - _lastForcedFlattenSkipLogUtc.Value).TotalMinutes >= FORCED_FLATTEN_SKIP_LOG_INTERVAL_MINUTES;
                        if (shouldLogSkip && _streams.Values.Any(s => s.Session == sessionClass && !s.Committed))
                        {
                            _lastForcedFlattenSkipLogUtc = utcNow;
                            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "FORCED_FLATTEN_SKIP_NO_CACHE", state: "ENGINE",
                                new
                                {
                                    session_class = sessionClass,
                                    trading_date = tradingDateStr,
                                    note = "Session close not resolved and fallback unavailable — forced flatten will not trigger"
                                }));
                        }
                        // SESSION_CLOSE_CACHE_MISSING: ERROR once per (tradingDay, sessionClass) when cache empty 5+ min
                        if (!_firstSessionCloseCacheMissUtc.HasValue)
                            _firstSessionCloseCacheMissUtc = utcNow;
                        var minutesMissing = (utcNow - _firstSessionCloseCacheMissUtc.Value).TotalMinutes;
                        if (minutesMissing >= SESSION_CLOSE_CACHE_MISSING_ALERT_MINUTES)
                        {
                            var cacheMissKey = (tradingDateStr, sessionClass);
                            bool shouldEmitCacheMiss;
                            lock (_engineLock)
                            {
                                shouldEmitCacheMiss = _sessionCloseCacheMissingEmittedKeys.Add(cacheMissKey);
                            }
                            if (shouldEmitCacheMiss)
                            {
                                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "SESSION_CLOSE_CACHE_MISSING", state: "ENGINE",
                                    new
                                    {
                                        instrument = _executionInstrument ?? "N/A",
                                        trading_date = tradingDateStr,
                                        session_class = sessionClass,
                                        minutes_missing = Math.Round(minutesMissing, 1),
                                        note = "Session close cache empty for 5+ minutes — forced flatten cannot trigger; watchdog alert"
                                    }));
                            }
                        }
                        continue;
                    }
                    if (!sessionTruth.HasSession || !sessionTruth.FlattenTriggerUtc.HasValue)
                        continue;
                    if (utcNow < sessionTruth.FlattenTriggerUtc.Value)
                        continue;

                    // Cache populated (or fallback used) — reset missing timer
                    _firstSessionCloseCacheMissUtc = null;

                    // Emit FORCED_FLATTEN_TRIGGERED once per (tradingDay, sessionClass)
                    if (!_journals.HasForcedFlattenTriggeredEmitted(tradingDateStr, sessionClass))
                    {
                        _journals.MarkForcedFlattenTriggeredEmitted(tradingDateStr, sessionClass);
                        var streamsInSession = _streams.Values.Where(s => s.Session == sessionClass && !s.Committed).ToList();
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "FORCED_FLATTEN_TRIGGERED", state: "ENGINE",
                            new
                            {
                                reason = "SESSION_CLOSE",
                                session_class = sessionClass,
                                instrument = _executionInstrument ?? "",
                                source = sessionTruth.Source,
                                authority_rank = sessionTruth.AuthorityRank,
                                resolved_session_close_utc = sessionTruth.ResolvedSessionCloseUtc?.ToString("o"),
                                buffer_seconds = sessionTruth.BufferSeconds,
                                trading_date = tradingDateStr,
                                streams_impacted = streamsInSession.Select(s => s.Stream).ToList(),
                                used_fallback = sessionTruth.UsedFallback,
                                conflict_detected = sessionTruth.ConflictDetected,
                                nt_failure_reason = sessionTruth.NtCacheResult?.FailureReason ?? ""
                            }));
                    }

                    foreach (var s in _streams.Values)
                    {
                        if (s.Session != sessionClass || s.Committed) continue;
                        s.HandleForcedFlatten(utcNow);
                    }

                    RunSessionCloseGlobalExposureSweep(tradingDateStr, sessionClass, utcNow);
                }
            }

            forcedFlattenMs = wForcedFlatten?.ElapsedMilliseconds ?? 0;
            Stopwatch? wTail = cpuProf ? Stopwatch.StartNew() : null;

            // Periodic stream status summary (diagnostic only, rate-limited to every 5 minutes)
            if (_loggingConfig.DiagnosticsEnabled && _streams.Count > 0)
            {
                var timeSinceLastSummary = (_lastStreamStatusSummaryUtc == null)
                    ? double.MaxValue
                    : (utcNow - _lastStreamStatusSummaryUtc.Value).TotalMinutes;
                if (timeSinceLastSummary >= STREAM_STATUS_SUMMARY_INTERVAL_MINUTES)
                {
                    _lastStreamStatusSummaryUtc = utcNow;
                    LogStreamStatusSummary(utcNow);
                }
            }

            // Track and log gap violations across all streams (rate-limited to once per 5 minutes)
            var timeSinceLastGapViolationSummary = (utcNow - (_lastGapViolationSummaryUtc ?? DateTimeOffset.MinValue)).TotalMinutes;
            if (timeSinceLastGapViolationSummary >= 5.0 || _lastGapViolationSummaryUtc == null)
            {
                var invalidatedStreams = _streams.Values
                    .Where(s => s.RangeInvalidated && !s.Committed)
                    .Select(s => new
                    {
                        stream_id = s.Stream,
                        instrument = s.Instrument,
                        session = s.Session,
                        slot_time = s.SlotTimeChicago,
                        state = s.State.ToString()
                    })
                    .ToList();

                if (invalidatedStreams.Count > 0)
                {
                    _lastGapViolationSummaryUtc = utcNow;
                    LogEngineEvent(utcNow, "GAP_VIOLATIONS_SUMMARY", new
                    {
                        invalidated_stream_count = invalidatedStreams.Count,
                        invalidated_streams = invalidatedStreams,
                        total_streams = _streams.Count,
                        note = "Streams invalidated due to gap tolerance violations - trading blocked for these streams"
                    });
                }
            }

            // Health monitor: evaluate data loss (rate-limited internally)
            _healthMonitor?.Evaluate(utcNow);
            if (IsTerminalShutdownLatched() || TryRespectRunWideShutdownSignal(utcNow, "post_health_monitor"))
                return;

            // Gap 3 Phase 3: Protective coverage audit metrics (rate-limited by coordinator)
            if (_lastAuditMetricsEmitWallUtc == DateTimeOffset.MinValue ||
                (nowWall - _lastAuditMetricsEmitWallUtc).TotalSeconds >= AuditMetricsEmitWallIntervalSeconds)
            {
                _lastAuditMetricsEmitWallUtc = nowWall;
                var emProt = _runtimeAudit != null ? RuntimeAuditHub.CpuStart() : 0L;
                _protectiveCoordinator?.EmitMetrics(utcNow);
                if (emProt != 0)
                    _runtimeAudit?.CpuEnd(emProt, RuntimeAuditSubsystem.EmitMetricsProtective);
                var emMis = _runtimeAudit != null ? RuntimeAuditHub.CpuStart() : 0L;
                _mismatchCoordinator?.EmitMetrics(utcNow);
                if (emMis != 0)
                    _runtimeAudit?.CpuEnd(emMis, RuntimeAuditSubsystem.EmitMetricsMismatch);
            }
            if (IsTerminalShutdownLatched() || TryRespectRunWideShutdownSignal(utcNow, "post_metrics_emit"))
                return;

            _runtimeAudit?.TryEmitPeriodicWallClock(nowWall);

            if (_executionAdapter is NinjaTraderSimAdapter ingressAdapter)
                ingressAdapter.DrainCallbackIngress(nowWall);
            if (IsTerminalShutdownLatched() || TryRespectRunWideShutdownSignal(utcNow, "post_callback_ingress_drain"))
                return;

            tailCoordinatorsMs = wTail?.ElapsedMilliseconds ?? 0;
            if (cpuProf && !isHistorical &&
                (_lastEngineCpuProfileUtc == DateTimeOffset.MinValue ||
                 (nowWall - _lastEngineCpuProfileUtc).TotalSeconds >= EngineCpuProfileEmitIntervalSeconds))
            {
                _lastEngineCpuProfileUtc = nowWall;
                var obMs = Interlocked.Exchange(ref _cpuProfileOnBarLockMsAccum, 0L);
                var obCnt = Interlocked.Exchange(ref _cpuProfileOnBarCount, 0);
                var lockSumMs = preReconMs + reconciliationRunnerMs + timetableReloadMs + streamTickMs +
                                secondReconciliationMs + forcedFlattenMs + tailCoordinatorsMs;
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ENGINE_CPU_PROFILE_LOCK_SLICES", state: "ENGINE",
                    new
                    {
                        pre_lock_ms = preLockMs,
                        pre_reconciliation_ms = preReconMs,
                        reconciliation_runner_ms = reconciliationRunnerMs,
                        timetable_reload_ms = timetableReloadMs,
                        stream_tick_ms = streamTickMs,
                        second_reconciliation_ms = secondReconciliationMs,
                        forced_flatten_ms = forcedFlattenMs,
                        tail_coordinators_ms = tailCoordinatorsMs,
                        lock_sum_ms = lockSumMs,
                        stream_count = _streams.Count,
                        onbar_lock_ms_window = obMs,
                        onbar_calls_window = obCnt,
                        onbar_avg_lock_ms = obCnt > 0 ? Math.Round((double)obMs / obCnt, 2) : 0,
                        note = "Wall-clock slices inside engine lock + OnBar totals since last emit; touch data/engine_cpu_profile.enabled to enable. Subsystem ENGINE_CPU_PROFILE is emitted by RuntimeAuditHub."
                    }));
            }
            }
            finally
            {
                if (lockRegionStart != 0)
                    _runtimeAudit?.CpuEnd(lockRegionStart, RuntimeAuditSubsystem.EngineLockRegion);
            }
        }
        }
        finally
        {
            if (tickTotalStart != 0)
                _runtimeAudit?.CpuEnd(tickTotalStart, RuntimeAuditSubsystem.EngineTickTotal);
        }
    }




}

/// <summary>
/// Trigger file format for manual force-reconcile of orphan journals.
/// Create data/pending_force_reconcile.json with this structure.
/// </summary>
internal sealed class ForceReconcileTrigger
{
    public List<string> instruments { get; set; } = new();
}
