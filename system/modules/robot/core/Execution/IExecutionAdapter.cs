using System;
using System.Collections.Generic;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Execution adapter interface for broker-agnostic order placement.
/// RobotEngine calls adapters when it wants to place/modify orders.
/// Adapters handle broker-specific details.
///
/// AUDIT RULE: Strategy layers should NOT call adapter.Flatten, adapter.SubmitEntryOrders,
/// or adapter.CancelOrders directly. Use EnqueueExecutionCommand instead so execution flows
/// through the IEA as the single authority. The adapter is transport-only; IEA orchestrates.
/// </summary>
public interface IExecutionAdapter
{
    /// <summary>
    /// Place an entry order (market, limit, or stop-market).
    /// </summary>
    /// <param name="intentId">Unique intent identifier (hash of canonical fields)</param>
    /// <param name="instrument">Instrument symbol</param>
    /// <param name="direction">"Long" or "Short"</param>
    /// <param name="entryPrice">Entry price (null for market order, used as limit price for Limit orders, stop price for StopMarket orders)</param>
    /// <param name="quantity">Number of contracts</param>
    /// <param name="entryOrderType">Order type: "LIMIT", "STOP_MARKET", or "MARKET" (null defaults to Limit if entryPrice provided, Market if null)</param>
    /// <param name="ocoGroup">Optional OCO group to link with paired entry (e.g. when breakout crossed, MARKET + STOP need same OCO)</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Order submission result</returns>
    OrderSubmissionResult SubmitEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal? entryPrice,
        int quantity,
        string? entryOrderType,
        string? ocoGroup,
        DateTimeOffset utcNow);

    /// <summary>
    /// Place a stop-market entry order (breakout stop).
    /// Intended for placing bracketed stop entries immediately after RANGE_LOCKED.
    /// </summary>
    /// <param name="intentId">Unique intent identifier (hash of canonical fields)</param>
    /// <param name="instrument">Instrument symbol</param>
    /// <param name="direction">"Long" or "Short"</param>
    /// <param name="stopPrice">Stop trigger price</param>
    /// <param name="quantity">Number of contracts</param>
    /// <param name="ocoGroup">Optional OCO group string to link long/short entries</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Order submission result</returns>
    OrderSubmissionResult SubmitStopEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow);

    /// <summary>
    /// Place protective stop order (must be placed immediately after entry submission or fill).
    /// </summary>
    /// <param name="intentId">Intent identifier</param>
    /// <param name="instrument">Instrument symbol</param>
    /// <param name="direction">"Long" or "Short"</param>
    /// <param name="stopPrice">Stop loss price</param>
    /// <param name="quantity">Number of contracts</param>
    /// <param name="ocoGroup">OCO group identifier (for pairing with target order). If null, no OCO pairing.</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Order submission result</returns>
    OrderSubmissionResult SubmitProtectiveStop(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow);

    /// <summary>
    /// Place target order (profit target).
    /// </summary>
    /// <param name="intentId">Intent identifier</param>
    /// <param name="instrument">Instrument symbol</param>
    /// <param name="direction">"Long" or "Short"</param>
    /// <param name="targetPrice">Target price</param>
    /// <param name="quantity">Number of contracts</param>
    /// <param name="ocoGroup">OCO group identifier (for pairing with stop order). If null, no OCO pairing.</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Order submission result</returns>
    OrderSubmissionResult SubmitTargetOrder(
        string intentId,
        string instrument,
        string direction,
        decimal targetPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow);

    /// <summary>
    /// Modify stop to break-even (when BE trigger is reached).
    /// </summary>
    /// <param name="intentId">Intent identifier</param>
    /// <param name="instrument">Instrument symbol</param>
    /// <param name="beStopPrice">Break-even stop price (entry ± 1 tick)</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Order modification result</returns>
    OrderModificationResult ModifyStopToBreakEven(
        string intentId,
        string instrument,
        decimal beStopPrice,
        DateTimeOffset utcNow);

    /// <summary>
    /// Cancel all orders and flatten position for this intent.
    /// </summary>
    /// <param name="intentId">Intent identifier</param>
    /// <param name="instrument">Instrument symbol</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Flatten result</returns>
    FlattenResult Flatten(
        string intentId,
        string instrument,
        DateTimeOffset utcNow);
    
    /// <summary>
    /// Emergency flatten by instrument when IEA is blocked. Prefer <see cref="TryEnqueueEmergencyFlattenProtective"/> from timer/worker threads (SIM).
    /// </summary>
    /// <param name="instrument">Execution instrument key (e.g. MYM, MNQ)</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Flatten result</returns>
    FlattenResult FlattenEmergency(string instrument, DateTimeOffset utcNow);

    /// <summary>
    /// Thread-safe: enqueue EMERGENCY <c>NtFlattenInstrumentCommand</c> for strategy-thread drain (SIM).
    /// Returns false when not supported (e.g. LIVE stub).
    /// </summary>
    bool TryEnqueueEmergencyFlattenProtective(string instrument, DateTimeOffset utcNow);

    /// <summary>
    /// Get account snapshot (positions and working orders) for recovery reconciliation.
    /// </summary>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Account snapshot</returns>
    AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow);

    /// <summary>
    /// Get current market bid/ask for breakout validity gate. Returns (null, null) when unavailable (gate skips, fail open).
    /// </summary>
    (decimal? Bid, decimal? Ask) GetCurrentMarketPrice(string instrument, DateTimeOffset utcNow);
    
    /// <summary>
    /// Cancel robot-owned working orders only (strict prefix matching: "QTSW2:").
    /// </summary>
    /// <param name="snap">Account snapshot from GetAccountSnapshot</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow);

    /// <summary>
    /// Cancel specific orders by broker order IDs (stream-scoped recovery, e.g. CancelAndRebuild).
    /// </summary>
    /// <param name="orderIds">Broker order IDs to cancel</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    void CancelOrders(IEnumerable<string> orderIds, DateTimeOffset utcNow);

    /// <summary>
    /// Phase 3: Evaluate break-even triggers. When IEA enabled, delegates to IEA. Uses tick de-dupe when tickTimeFromEvent provided.
    /// </summary>
    /// <param name="tickPrice">Current tick price</param>
    /// <param name="tickTimeFromEvent">Event timestamp from market data (for de-dupe). Null = use UtcNow fallback.</param>
    /// <param name="executionInstrument">Execution instrument filter (e.g. MNQ, MGC)</param>
    void EvaluateBreakEven(decimal tickPrice, DateTimeOffset? tickTimeFromEvent, string executionInstrument) { }

    /// <summary>
    /// Phase 1: Process pending unresolved executions (non-blocking grace retry). Called from strategy thread on OnBarUpdate/OnMarketData.
    /// Non-IEA path only; IEA uses queue-based retry.
    /// </summary>
    void ProcessPendingUnresolvedExecutions() { }

    /// <summary>
    /// Phase 3: Request recovery for instrument. Routes to IEA when bound. No-op for adapters without IEA.
    /// </summary>
    void RequestRecoveryForInstrument(string instrument, string reason, object context, DateTimeOffset utcNow) { }

    /// <summary>
    /// Phase 5: Request supervisory action for instrument. Routes to IEA when bound. No-op for adapters without IEA.
    /// </summary>
    void RequestSupervisoryActionForInstrument(string instrument, SupervisoryTriggerReason reason, SupervisorySeverity severity, object? context, DateTimeOffset utcNow) { }

    /// <summary>
    /// Enqueue execution command for IEA processing. Strategy layers should use this instead of calling
    /// adapter.Flatten/SubmitOrders/CancelOrders directly. Adapters with IEA forward to IEA; others no-op.
    /// </summary>
    void EnqueueExecutionCommand(ExecutionCommandBase command);

    /// <summary>
    /// Intent IDs relevant to protective audit + registry retention for this instrument:
    /// union of BE-monitoring actives (filled, open) and adoption candidates (EntrySubmitted, !TradeCompleted).
    /// Aligns expected intent context with state-consistency release semantics.
    /// </summary>
    IReadOnlyCollection<string> GetActiveIntentIdsForProtectiveAudit(string instrument) => Array.Empty<string>();

    /// <summary>
    /// Open filled intent IDs for this instrument, including intents whose BE stop was already modified.
    /// This is ownership/exposure context, not a "needs BE" filter.
    /// </summary>
    IReadOnlyCollection<string> GetOpenIntentIdsForInstrument(string instrument) => Array.Empty<string>();

    /// <summary>
    /// Broker shows open position but journal open filled sum is zero: attempt tagged recovery journal upsert when
    /// robot ownership evidence is strong (tags, OrderMap, adoption candidates). Not used for empty decoded intent (untracked path).
    /// </summary>
    /// <returns>True when a qualifying journal row was written.</returns>
    bool TryRepairTaggedBrokerWithoutJournal(string instrument, int accountQtyAbs, int journalOpenQtySum, DateTimeOffset utcNow, out string resultCode, out string? detail)
    {
        resultCode = "NOT_IMPLEMENTED";
        detail = null;
        return false;
    }

    /// <summary>
    /// Retry deferred adoption scan when candidates were empty but broker had orders (journal load race).
    /// Call from periodic path (e.g. reconciliation) so retry does not depend only on execution updates.
    /// No-op for adapters without IEA.
    /// </summary>
    void TryRetryDeferredAdoptionScan() { }

    /// <summary>
    /// Before mismatch assembly: all IEAs for this instrument reclassify recoverable UNOWNED rows and attempt broker-id alias links from snapshot.
    /// Does not submit, modify, or cancel orders. No-op when registry unavailable.
    /// </summary>
    void PrepareOrderRegistryForMismatchAssembly(string instrument, AccountSnapshot snap, DateTimeOffset utcNow) { }

    /// <summary>
    /// Session-close flatten: enqueue cancel+flatten NtActions (no drain). Strategy thread drains the queue.
    /// Broker-flat confirmation is delivered later by execution/reconciliation; callers must not block waiting on NT.
    /// Returns null if not supported (caller should use an enqueue-safe fallback when available).
    /// </summary>
    FlattenResult? RequestSessionCloseFlattenImmediate(string intentId, string instrument, DateTimeOffset utcNow);

    /// <summary>
    /// Session-close cancel-only companion for sibling intents before a single broker-level flatten.
    /// Implementations should enqueue onto the strategy thread and return without touching NT synchronously.
    /// </summary>
    int RequestSessionCloseCancelIntents(IEnumerable<string> intentIds, string instrument, DateTimeOffset utcNow) => 0;

    /// <summary>
    /// Hard fail-closed: broker flatten once per instrument. Harness/default adapters do not touch broker state.
    /// </summary>
    bool TryTriggerHardFlatten(string instrument, string reason, DateTimeOffset utcNow) => false;

    /// <summary>
    /// True when an untagged execution fill is likely the close from a recent self-initiated <c>Account.Flatten</c> (full NT builds).
    /// Default false for harness / stubs.
    /// </summary>
    bool TryRecognizeSelfInitiatedFlattenCloseFill(string instrument, DateTimeOffset utcNow) => false;

    /// <summary>
    /// True when the adapter is safe to drive real or simulated submission paths (NT context wired; SIM verified when applicable).
    /// Used to gate pre-hydration and bracket submission during startup before DataLoaded wiring completes.
    /// </summary>
    bool IsExecutionContextReady { get; }
}

/// <summary>
/// Account snapshot for recovery reconciliation.
/// </summary>
public class AccountSnapshot
{
    public List<PositionSnapshot>? Positions { get; set; }
    public List<WorkingOrderSnapshot>? WorkingOrders { get; set; }

    /// <summary>Wall-clock UTC when positions/orders were read from the broker (execution safety gate freshness).</summary>
    public DateTimeOffset? CapturedAtUtc { get; set; }

    /// <summary>False when the adapter could not read the broker state and returned a placeholder or partial snapshot.</summary>
    public bool IsAuthoritative { get; set; } = true;

    /// <summary>Diagnostic reason when <see cref="IsAuthoritative"/> is false.</summary>
    public string? NonAuthoritativeReason { get; set; }
}

/// <summary>
/// Position snapshot from broker account.
/// </summary>
public class PositionSnapshot
{
    /// <summary>NT MasterInstrument.Name — canonical bucket key for <see cref="BrokerPositionResolver"/>.</summary>
    public string Instrument { get; set; } = "";
    public int Quantity { get; set; }
    public decimal AveragePrice { get; set; }

    /// <summary>Optional contract label for audit logs (e.g. NT Instrument.FullName). Bucketing uses <see cref="Instrument"/> only.</summary>
    public string? ContractLabel { get; set; }

    /// <summary>Broker-side market position label when available (e.g. NT MarketPosition).</summary>
    public string? MarketPosition { get; set; }
}

/// <summary>
/// Working order snapshot from broker account.
/// </summary>
public class WorkingOrderSnapshot
{
    public string OrderId { get; set; } = "";
    public string Instrument { get; set; } = "";
    public string? Tag { get; set; }
    public string? OcoGroup { get; set; }
    public string? OrderType { get; set; }
    public decimal? Price { get; set; }
    public decimal? StopPrice { get; set; }
    public int Quantity { get; set; }
}
