// CRITICAL: Define NINJATRADER for NinjaTrader's compiler
// NinjaTrader compiles to tmp folder and may not respect .csproj DefineConstants
#define NINJATRADER

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// NinjaTrader Sim adapter: places orders on NT Simulation / Playback (non-live) accounts only.
/// 
/// Submission Sequencing (Safety-First Approach):
/// 1. Submit entry order (market order initially)
/// 2. On entry fill confirmation → submit protective stop + target (OCO pair)
/// 3. On BE trigger reached → modify stop to break-even
/// 4. On target/stop fill → flatten remaining position
/// 
/// Hard Safety Requirements:
/// - Must verify non-live account usage (fail closed if live / disallowed)
/// - All orders must be namespaced by (intent_id, stream) for isolation
/// - OCO grouping must be stream-local (no cross-stream interference)
/// </summary>
public sealed partial class NinjaTraderSimAdapter : IExecutionAdapter, IIEAOrderExecutor, INtActionExecutor, INtMarketReentryExecutionGate, IIntentRegistrationAdapter
{
    internal static bool ShouldDeferReentryProtectionForPartialFill(int cumulativeFilledQuantity, int expectedQuantity) =>
        expectedQuantity > 0 &&
        cumulativeFilledQuantity > 0 &&
        cumulativeFilledQuantity < expectedQuantity;

    internal static bool ShouldTerminalizeAfterExitFill(ExecutionJournalEntry? entry) =>
        entry != null &&
        entry.EntryFilledQuantityTotal > 0 &&
        entry.ExitFilledQuantityTotal >= entry.EntryFilledQuantityTotal;

    /// <summary>At most one delayed critical flatten re-enqueue per (account, canonical) until the retry fires (stale-owner takeover window).</summary>
    private readonly ConcurrentDictionary<string, byte> _criticalFlattenCoordinationRetryInflight = new(StringComparer.OrdinalIgnoreCase);

    private static int _adapterInstanceCounter;
    private readonly int _adapterInstanceId;

    private readonly RobotLogger _log;
    /// <summary>Repository root (market data, configs). Reserved for repo-scoped paths.</summary>
    private readonly string _repositoryRoot;
    /// <summary>Persistence root: execution incidents, trace, journal file reads — matches <see cref="ExecutionJournal"/> / engine <c>_persistenceBase</c>.</summary>
    private readonly string _stateRoot;
    private readonly ExecutionJournal _executionJournal;
    private readonly ExecutionTraceWriter? _executionTrace;
    /// <summary>G4: Durable broker-flat completion lines (canonical reconciliation abs == 0), not submit-only.</summary>
    private readonly FlattenCompletionDecisionLog _flattenCompletionDecisionLog;

    /// <summary>50ms duplicate suppression for identical NT order updates (instrument + order_id + order_state).</summary>
    private readonly object _callbackDedupLock = new();
    /// <summary>Permanent dedup: first-seen (instrument + execution_id + fill_qty) per process; allows distinct partials (different ids or qty).</summary>
    private readonly ConcurrentDictionary<string, byte> _permanentExecutionProcessed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _permanentExecutionDedupSkipCount = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Final idempotency guard for robot-owned flatten fill post-processing.</summary>
    private readonly ConcurrentDictionary<string, byte> _flattenExecutionPostFillProcessed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _lateSessionCloseFlattenConfirmed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OrderCallbackDedupEntry> _orderCallbackDedup50ms = new(StringComparer.OrdinalIgnoreCase);

    private sealed class OrderCallbackDedupEntry
    {
        public DateTimeOffset LastUtc;
        public long TotalSkips;
    }

    /// <summary>Avoid immediate repeat of tagged repair when broker/journal evidence unchanged (e.g. non-owning charts).</summary>
    private sealed class TaggedRepairCooldownEntry
    {
        public DateTimeOffset LastFailureUtc;
        public int AccountQty;
        public int JournalQty;
        public string LastFailureResultCode = "";
    }

    private readonly object _taggedRepairCooldownLock = new();
    private readonly Dictionary<string, TaggedRepairCooldownEntry> _taggedRepairCooldownByInstrument =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan TaggedRepairFailureCooldownWindow = TimeSpan.FromSeconds(3);

    private static bool IsTaggedRepairFailureCooldownEligible(string code) =>
        string.Equals(code, "NO_QUALIFYING_INTENT_CONTEXT", StringComparison.OrdinalIgnoreCase)
        || string.Equals(code, "NO_OWNERSHIP_EVIDENCE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(code, "SNAPSHOT_ZERO_POSITION", StringComparison.OrdinalIgnoreCase);

    private bool TrySuppressTaggedRepairRepeatedFailure(string instTrim, int accountQtyAbs, int journalOpenQtySum,
        DateTimeOffset utcNow, out string resultCode)
    {
        resultCode = "";
        lock (_taggedRepairCooldownLock)
        {
            if (!_taggedRepairCooldownByInstrument.TryGetValue(instTrim, out var prev)) return false;
            if (!IsTaggedRepairFailureCooldownEligible(prev.LastFailureResultCode)) return false;
            if (prev.AccountQty != accountQtyAbs || prev.JournalQty != journalOpenQtySum) return false;
            if ((utcNow - prev.LastFailureUtc) >= TaggedRepairFailureCooldownWindow) return false;
            resultCode = "SUPPRESSED_TAGGED_REPAIR_COOLDOWN";
            return true;
        }
    }

    private void RecordTaggedRepairFailureForCooldown(string instTrim, int accountQtyAbs, int journalOpenQtySum,
        DateTimeOffset utcNow, string failureResultCode)
    {
        if (!IsTaggedRepairFailureCooldownEligible(failureResultCode)) return;
        lock (_taggedRepairCooldownLock)
        {
            _taggedRepairCooldownByInstrument[instTrim] = new TaggedRepairCooldownEntry
            {
                LastFailureUtc = utcNow,
                AccountQty = accountQtyAbs,
                JournalQty = journalOpenQtySum,
                LastFailureResultCode = failureResultCode
            };
        }
    }

    private void ClearTaggedRepairFailureCooldown(string instTrim)
    {
        lock (_taggedRepairCooldownLock)
        {
            _taggedRepairCooldownByInstrument.Remove(instTrim);
        }
    }

    // Order tracking: intentId -> NT order info (or IEA's when use_instrument_execution_authority)
    private readonly ConcurrentDictionary<string, OrderInfo> _orderMap = new();
    
    // Intent tracking: intentId -> Intent (or IEA's when IEA enabled)
    private readonly ConcurrentDictionary<string, Intent> _intentMap = new();
    
    // Fill callback: intentId -> callback action (for protective order submission)
    private readonly ConcurrentDictionary<string, Action<string, decimal, int, DateTimeOffset>> _fillCallbacks = new();
    
    // Intent policy tracking: intentId -> policy expectation (or IEA's when IEA enabled)
    private readonly Dictionary<string, IntentPolicyExpectation> _intentPolicy = new();
    
    // IEA: when enabled, use shared maps so all instances see same orders
    private bool _useInstrumentExecutionAuthority = false;
    private AggregationPolicy? _aggregationPolicy;
    private InstrumentExecutionAuthority? _iea;
    private string? _ieaAccountName;
    private string? _ieaEngineExecutionInstrument;
    private readonly Dictionary<string, DateTimeOffset> _lastIeaExecUpdateRoutedUtc = new();
    
    /// <summary>Investigation: NinjaTrader strategy instance id (set by host before SetNTContext).</summary>
    private string? _strategyInstanceIdForAudit;
    private bool _investigationRuntimeFingerprintEmitted;
    
    /// <summary>Effective order map: IEA's when enabled, else adapter's.</summary>
    private ConcurrentDictionary<string, OrderInfo> OrderMap => _useInstrumentExecutionAuthority && _iea != null ? _iea.OrderMap : _orderMap;
    /// <summary>Effective intent map: IEA's when enabled, else adapter's.</summary>
    private ConcurrentDictionary<string, Intent> IntentMap => _useInstrumentExecutionAuthority && _iea != null ? _iea.IntentMap : _intentMap;
    /// <summary>Effective intent policy: IEA's when enabled, else adapter's.</summary>
    private Dictionary<string, IntentPolicyExpectation> IntentPolicy => _useInstrumentExecutionAuthority && _iea != null ? _iea.IntentPolicy : _intentPolicy;
    
    // Track which intents have already triggered emergency (idempotent)
    private readonly HashSet<string> _emergencyTriggered = new();
    
    // NT Account and Instrument references (injected from Strategy host)
    private object? _ntAccount; // NinjaTrader.Cbi.Account
    private object? _ntInstrument; // NinjaTrader.Cbi.Instrument
    private bool _simAccountVerified = false;
    private bool _ntContextSet = false;

    private sealed class LatestMarketDataLast
    {
        public decimal Price;
        public DateTimeOffset Utc;
    }

    private static readonly TimeSpan LatestMarketDataLastFreshWindow = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, LatestMarketDataLast> _latestMarketDataLastByInstrument =
        new(StringComparer.OrdinalIgnoreCase);


    /// <summary>Callback when reentry order fills. Engine invokes HandleReentryFill on matching stream.</summary>
    private Action<string, DateTimeOffset>? _onReentryFillCallback;
    
    /// <summary>Callback when reentry protective bracket is accepted (both stop and target Working). Engine invokes HandleReentryProtectionAccepted on matching stream.</summary>
    private Action<string, DateTimeOffset>? _onReentryProtectionAcceptedCallback;

    /// <summary>Callback when the strategy thread accepts/rejects the market reentry submit.</summary>
    private Action<string, DateTimeOffset, bool, string?>? _onReentrySubmitCompletedCallback;

    /// <summary>Callback when a timed-out session-close flatten is later proven closed and broker-flat.</summary>
    private Action<string, string, DateTimeOffset>? _onSessionCloseFlattenConfirmedLateCallback;

    /// <summary>Callback to check if entry orders for a stream should be cancelled when position is flat (invalid lifecycle).
    /// Returns (ShouldCancel, Reason) - true when stream is no longer eligible (EXPIRED, COMMITTED, forced_flatten, etc.).</summary>
    private Func<string, string, (bool ShouldCancel, string? Reason)>? _shouldCancelEntryOrdersForStreamCallback;

    /// <summary>Callback to check if slot journal shows RANGE_LOCKED + StopBracketsSubmittedAtLock for a stream trading this instrument.
    /// Used at bootstrap to ADOPT working entry stops on restart instead of flattening.</summary>
    private Func<string, bool>? _hasSlotJournalWithEntryStopsForInstrumentCallback;

    /// <summary>Reentry intents for which we have already invoked the protection-accepted callback (idempotency).</summary>
    private readonly HashSet<string> _reentryProtectionAcceptedNotified = new(StringComparer.OrdinalIgnoreCase);

    private sealed class MarketReentryExecutionLatch
    {
        public string InstrumentKey = "";
        public string IntentId = "";
        public string? Stream;
        public string CorrelationId = "";
        public DateTimeOffset AcquiredAtUtc;
        public DateTimeOffset CommandUtc;
        public int DeferralCount;
    }

    private readonly object _marketReentryExecutionLatchLock = new();
    private readonly Dictionary<string, MarketReentryExecutionLatch> _marketReentryExecutionLatchByInstrument =
        new(StringComparer.OrdinalIgnoreCase);


    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingBECancelUtcByIntent = new(); // Replace-semantics: intent_id -> when old stop went CancelPending
    /// <summary>BE cancel+replace: intent_id -> (new_stop_order_id, start_utc). Used to log BE_CANCEL_REPLACE_STOP_WORKING and quantify overlap/gap.</summary>
    private readonly ConcurrentDictionary<string, (string NewStopOrderId, DateTimeOffset StartUtc)> _pendingBECancelReplaceByIntent = new(StringComparer.OrdinalIgnoreCase);
    private const int BE_REPLACE_CANCEL_WINDOW_SEC = 30;
    private const int BE_CONFIRM_TIMEOUT_SEC = 15;
    private const int BE_RETRY_INTERVAL_SEC = 5;
    private const int BE_MAX_RETRY_ATTEMPTS = 3;
    /// <param name="repositoryRoot">Project / repository root (e.g. market data).</param>
    /// <param name="stateRoot">Run-scoped persistence root (equals repository root unless isolated SIM playback).</param>
    public NinjaTraderSimAdapter(string repositoryRoot, string stateRoot, RobotLogger log, ExecutionJournal executionJournal)
    {
        _adapterInstanceId = System.Threading.Interlocked.Increment(ref _adapterInstanceCounter);
        _repositoryRoot = repositoryRoot;
        _stateRoot = stateRoot;
        _log = log;
        _executionJournal = executionJournal;
        _executionTrace = ExecutionTraceWriter.TryCreate(stateRoot);
        _flattenCompletionDecisionLog = new FlattenCompletionDecisionLog(stateRoot);

        // Note: SIM account verification happens when NT context is set via SetNTContext()
        // Mock mode has been removed - only real NT API execution is supported
    }
    

}

/// <summary>Pending BE modification request. Keyed by stop_order_id.</summary>
internal sealed class PendingBERequest
{
    public string IntentId { get; set; } = "";
    public string? OcoId { get; set; }
    public decimal RequestedStopPriceRaw { get; set; }
    public decimal RequestedStopPriceQuantized { get; set; }
    public DateTimeOffset RequestedUtc { get; set; }
    public string TradingDate { get; set; } = "";
    public string Stream { get; set; } = "";
    public string Instrument { get; set; } = "";
    public string ExecutionInstrumentKey { get; set; } = "";
    public int RetryCount { get; set; }
    public string RawTag { get; set; } = "";
    /// <summary>When set, we're waiting for retry time. Null = initial confirmation wait.</summary>
    public DateTimeOffset? RetryUtc { get; set; }
}
