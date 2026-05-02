using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Threading;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Execution;

public sealed partial class StreamStateMachine
{
    /// <summary>
    /// Helper to compute intent ID for restart recovery check.
    /// </summary>
    private string ComputeIntentId(string direction, decimal entryPrice, DateTimeOffset entryTimeUtc, string triggerReason)
    {
        var intent = new Intent(
            TradingDate,
            Stream,
            Instrument,
            ExecutionInstrument,
            Session,
            SlotTimeChicago,
            direction,
            entryPrice,
            stopPrice: null, // Not needed for intent ID computation
            targetPrice: null,
            beTrigger: null,
            entryTimeUtc,
            triggerReason);
        return intent.ComputeIntentId();
    }

    /// <summary>
    /// STRICT: True only if broker has exactly one valid entry-order set.
    /// Valid set = both orders exist, correct instrument, intent IDs, breakout prices, OCO linkage, no duplicates.
    /// Any incomplete, wrong-price, duplicate, or malformed structure → false.
    /// </summary>
    public bool HasValidEntryOrdersOnBroker(AccountSnapshot snap)
    {
        if (!RangeHigh.HasValue || !RangeLow.HasValue ||
            !_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
            return false;

        var longIntentId = ComputeIntentId("Long", _brkLongRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_LONG");
        var shortIntentId = ComputeIntentId("Short", _brkShortRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_SHORT");
        var brkLong = _brkLongRounded.Value;
        var brkShort = _brkShortRounded.Value;

        var working = snap.WorkingOrders ?? new List<WorkingOrderSnapshot>();
        WorkingOrderSnapshot? longOrder = null;
        WorkingOrderSnapshot? shortOrder = null;
        int longCount = 0, shortCount = 0;

        foreach (var o in working)
        {
            if (!IsSameInstrument(o.Instrument))
                continue;
            var tag = o.Tag ?? "";
            if (string.IsNullOrEmpty(tag) || tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase) || tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase))
                continue;
            var decoded = RobotOrderIds.DecodeIntentId(tag);
            if (decoded == longIntentId) { longCount++; longOrder = o; }
            else if (decoded == shortIntentId) { shortCount++; shortOrder = o; }
        }

        // INVARIANT: Exactly 1 long, 1 short. No partial, no duplicates.
        if (longCount != 1 || shortCount != 1)
            return false;

        // Strict: match expected breakout prices (StopPrice)
        if (longOrder != null && longOrder.StopPrice.HasValue && Math.Abs(longOrder.StopPrice.Value - brkLong) > 0.0001m)
            return false;
        if (shortOrder != null && shortOrder.StopPrice.HasValue && Math.Abs(shortOrder.StopPrice.Value - brkShort) > 0.0001m)
            return false;

        // OCO linkage: both must share same OcoGroup (if OCO is used)
        if (longOrder != null && shortOrder != null)
        {
            var ocoLong = longOrder.OcoGroup ?? "";
            var ocoShort = shortOrder.OcoGroup ?? "";
            if (!string.IsNullOrEmpty(ocoLong) && !string.IsNullOrEmpty(ocoShort) && ocoLong != ocoShort)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Get counts of matching entry orders (excludes protectives). Returns (longCount, shortCount, orderIds).
    /// </summary>
    private (int LongCount, int ShortCount, List<string> OrderIds) GetMatchingEntryOrderCounts(
        AccountSnapshot snap, string longIntentId, string shortIntentId)
    {
        var working = snap.WorkingOrders ?? new List<WorkingOrderSnapshot>();
        int longCount = 0, shortCount = 0;
        var orderIds = new List<string>();

        foreach (var o in working)
        {
            if (!IsSameInstrument(o.Instrument))
                continue;
            var tag = o.Tag ?? "";
            if (string.IsNullOrEmpty(tag) || tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase) || tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase))
                continue;
            var decoded = RobotOrderIds.DecodeIntentId(tag);
            if (decoded == longIntentId) { longCount++; orderIds.Add(o.OrderId ?? ""); }
            else if (decoded == shortIntentId) { shortCount++; orderIds.Add(o.OrderId ?? ""); }
        }
        return (longCount, shortCount, orderIds);
    }

    /// <summary>
    /// Phase A: Audit and classify broker state. Assign recovery action. Do NOT execute.
    /// Returns (reconciled: we checked, needsResubmit: action is ResubmitClean or CancelAndRebuild).
    /// </summary>
    public (bool Reconciled, bool NeedsResubmit) AuditAndClassifyEntryOrders(AccountSnapshot snap, DateTimeOffset utcNow)
    {
        if (Committed || State != StreamState.RANGE_LOCKED)
            return (false, false);
        if (_journal.ExecutionInterruptedByClose)
            return (false, false); // Safe by design: no entry resubmit when waiting for re-entry after forced flatten
        if (_entryDetected || utcNow >= MarketCloseUtc)
            return (false, false);
        if (!RangeHigh.HasValue || !RangeLow.HasValue || !_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
            return (false, false);

        var longIntentId = ComputeIntentId("Long", _brkLongRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_LONG");
        var shortIntentId = ComputeIntentId("Short", _brkShortRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_SHORT");
        var expectedLongPrice = _brkLongRounded.Value;
        var expectedShortPrice = _brkShortRounded.Value;

        var posQty = snap.Positions?.Where(p => IsSameInstrument(p.Instrument)).Sum(p => p.Quantity) ?? 0;
        var classification = ClassifyBrokerState(snap, longIntentId, shortIntentId, expectedLongPrice, expectedShortPrice, posQty);

        _entryOrderRecoveryState.LastClassificationUtc = utcNow;
        _entryOrderRecoveryState.LastClassificationResult = classification.ToString();

        switch (classification)
        {
            case BrokerStateClassification.PositionExists:
                return (true, false);

            case BrokerStateClassification.ValidSetPresent:
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDER_SET_VALID", State.ToString(),
                    new { stream_id = Stream, trading_date = TradingDate, long_intent_id = longIntentId, short_intent_id = shortIntentId }));
                ClearRecoveryAction(utcNow);
                return (true, false);

            case BrokerStateClassification.MissingSet:
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDER_SET_MISSING", State.ToString(),
                    new { stream_id = Stream, trading_date = TradingDate, long_intent_id = longIntentId, short_intent_id = shortIntentId, expected_long_price = expectedLongPrice, expected_short_price = expectedShortPrice }));
                SetRecoveryAction(EntryOrderRecoveryAction.ResubmitClean, "missing", utcNow);
                return (true, true);

            case BrokerStateClassification.BrokenSet:
                var (longCount, shortCount, orderIds) = GetMatchingEntryOrderCounts(snap, longIntentId, shortIntentId);
                var reason = longCount + shortCount == 0 ? "missing" : longCount + shortCount == 1 ? "partial" : "duplicate_or_invalid";
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    longCount + shortCount > 2 ? "ENTRY_ORDER_SET_DUPLICATE_DETECTED" : "ENTRY_ORDER_SET_BROKEN", State.ToString(),
                    new { stream_id = Stream, trading_date = TradingDate, long_intent_id = longIntentId, short_intent_id = shortIntentId, actual_long_count = longCount, actual_short_count = shortCount, order_ids = orderIds, reason }));
                SetRecoveryAction(EntryOrderRecoveryAction.CancelAndRebuild, reason, utcNow);
                return (true, true);
        }

        return (true, false);
    }

    /// <summary>
    /// Phase A backward compatibility: ReconcileEntryOrders delegates to AuditAndClassifyEntryOrders.
    /// </summary>
    public (bool Reconciled, bool NeedsResubmit) ReconcileEntryOrders(AccountSnapshot snap, DateTimeOffset utcNow)
        => AuditAndClassifyEntryOrders(snap, utcNow);

    /// <summary>
    /// Classify broker state for this stream.
    /// </summary>
    public BrokerStateClassification ClassifyBrokerState(AccountSnapshot snap, string longIntentId, string shortIntentId)
    {
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
            return BrokerStateClassification.MissingSet;
        var posQty = snap.Positions?.Where(p => IsSameInstrument(p.Instrument)).Sum(p => p.Quantity) ?? 0;
        return ClassifyBrokerState(snap, longIntentId, shortIntentId, _brkLongRounded.Value, _brkShortRounded.Value, posQty);
    }

    private BrokerStateClassification ClassifyBrokerState(AccountSnapshot snap, string longIntentId, string shortIntentId, decimal expectedLongPrice, decimal expectedShortPrice, int posQty)
    {
        if (posQty != 0 || _entryDetected)
            return BrokerStateClassification.PositionExists;

        var (longCount, shortCount, _) = GetMatchingEntryOrderCounts(snap, longIntentId, shortIntentId);
        var total = longCount + shortCount;

        if (longCount == 1 && shortCount == 1)
        {
            var working = snap.WorkingOrders ?? new List<WorkingOrderSnapshot>();
            WorkingOrderSnapshot? longOrder = null, shortOrder = null;
            foreach (var o in working)
            {
                if (!IsSameInstrument(o.Instrument)) continue;
                var tag = o.Tag ?? "";
                if (string.IsNullOrEmpty(tag) || tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase) || tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase)) continue;
                var decoded = RobotOrderIds.DecodeIntentId(tag);
                if (decoded == longIntentId) longOrder = o;
                else if (decoded == shortIntentId) shortOrder = o;
            }
            if (longOrder != null && longOrder.StopPrice.HasValue && Math.Abs(longOrder.StopPrice.Value - expectedLongPrice) > 0.0001m)
                return BrokerStateClassification.BrokenSet;
            if (shortOrder != null && shortOrder.StopPrice.HasValue && Math.Abs(shortOrder.StopPrice.Value - expectedShortPrice) > 0.0001m)
                return BrokerStateClassification.BrokenSet;
            if (longOrder != null && shortOrder != null)
            {
                var ocoLong = longOrder.OcoGroup ?? "";
                var ocoShort = shortOrder.OcoGroup ?? "";
                if (!string.IsNullOrEmpty(ocoLong) && !string.IsNullOrEmpty(ocoShort) && ocoLong != ocoShort)
                    return BrokerStateClassification.BrokenSet;
            }
            return BrokerStateClassification.ValidSetPresent;
        }

        if (total == 0)
            return BrokerStateClassification.MissingSet;

        return BrokerStateClassification.BrokenSet;
    }

    private void SetRecoveryAction(EntryOrderRecoveryAction action, string reason, DateTimeOffset utcNow)
    {
        _entryOrderRecoveryState = new EntryOrderRecoveryState { Action = action, Reason = reason, IssuedUtc = utcNow };
        _journal.RecoveryAction = action.ToString();
        _journal.RecoveryActionReason = reason;
        _journal.RecoveryActionIssuedUtc = utcNow.ToString("o");
        _journals.Save(_journal);
        _stopBracketsSubmittedAtLock = false;
        _journal.StopBracketsSubmittedAtLock = false;
        _journals.Save(_journal);
    }

    private void ClearRecoveryAction(DateTimeOffset utcNow)
    {
        if (_entryOrderRecoveryState.Action == EntryOrderRecoveryAction.None) return;
        _entryOrderRecoveryState = EntryOrderRecoveryState.None();
        _journal.RecoveryAction = "None";
        _journal.RecoveryActionReason = null;
        _journal.RecoveryActionIssuedUtc = null;
        _journals.Save(_journal);
    }

    /// <summary>
    /// Get broker order IDs for this stream's entry orders (for cancel).
    /// </summary>
    public List<string> GetEntryOrderIdsForStream(AccountSnapshot snap)
    {
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
            return new List<string>();
        var longIntentId = ComputeIntentId("Long", _brkLongRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_LONG");
        var shortIntentId = ComputeIntentId("Short", _brkShortRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_SHORT");
        var (_, _, orderIds) = GetMatchingEntryOrderCounts(snap, longIntentId, shortIntentId);
        return orderIds;
    }

    /// <summary>P2.10: Single tick-distance rule vs breakout stops (parity: <see cref="ParityInstrument.breakout_validity_tolerance_ticks"/>).</summary>
    private int GetBreakoutValidityToleranceTicks()
    {
        if (_spec != null && _spec.TryGetInstrument(Instrument, out var inst))
            return Math.Max(1, inst.breakout_validity_tolerance_ticks);
        return 2;
    }

    /// <summary>P2.10 mandatory core: same math for every path; missing-quote policy is caller-controlled.</summary>
    private bool IsBreakoutStillValidForEntry(
        decimal? bid,
        decimal? ask,
        decimal brkLong,
        decimal brkShort,
        int toleranceTicks,
        bool failClosedOnMissingQuotes)
    {
        if (!bid.HasValue && !ask.HasValue)
            return !failClosedOnMissingQuotes;
        var tolerance = toleranceTicks * _tickSize;
        var longInvalid = ask.HasValue && ask.Value >= brkLong + tolerance;
        var shortInvalid = bid.HasValue && bid.Value <= brkShort - tolerance;
        return !(longInvalid || shortInvalid);
    }

    private enum BreakoutEntrySubmitAction
    {
        Stop,
        Market,
        Reject
    }

    private (BreakoutEntrySubmitAction Action, decimal? DistanceTicks, string? Reason) ResolveBreakoutEntrySubmitAction(
        string direction,
        decimal breakoutPrice,
        decimal? bid,
        decimal? ask,
        int toleranceTicks)
    {
        decimal? marketPrice = string.Equals(direction, "Long", StringComparison.OrdinalIgnoreCase) ? ask : bid;
        if (!marketPrice.HasValue || _tickSize <= 0)
            return (BreakoutEntrySubmitAction.Stop, null, null);

        var crossed = string.Equals(direction, "Long", StringComparison.OrdinalIgnoreCase)
            ? marketPrice.Value >= breakoutPrice
            : marketPrice.Value <= breakoutPrice;
        if (!crossed)
            return (BreakoutEntrySubmitAction.Stop, null, null);

        var distanceTicks = string.Equals(direction, "Long", StringComparison.OrdinalIgnoreCase)
            ? (marketPrice.Value - breakoutPrice) / _tickSize
            : (breakoutPrice - marketPrice.Value) / _tickSize;

        if (distanceTicks <= toleranceTicks)
            return (BreakoutEntrySubmitAction.Market, distanceTicks, "crossed_within_tolerance");

        return (BreakoutEntrySubmitAction.Reject, distanceTicks, "crossed_beyond_tolerance");
    }

    /// <summary>
    /// P2.10 unified gate: logs <c>BREAKOUT_VALIDATED_UNIFIED</c> / <c>BREAKOUT_INVALIDATED_UNIFIED</c>.
    /// Returns true if valid or gate N/A (no breakout levels). Returns false if blocked.
    /// </summary>
    private bool LogAndEvaluateUnifiedBreakoutEntryValidity(
        DateTimeOffset utcNow,
        string path,
        bool failClosedOnMissingQuotes,
        decimal? brkLongOverride = null,
        decimal? brkShortOverride = null,
        decimal? bidOverride = null,
        decimal? askOverride = null)
    {
        var brkLong = brkLongOverride ?? _brkLongRounded;
        var brkShort = brkShortOverride ?? _brkShortRounded;
        if (!brkLong.HasValue || !brkShort.HasValue)
            return true;

        var toleranceTicks = GetBreakoutValidityToleranceTicks();
        decimal? bid = bidOverride;
        decimal? ask = askOverride;
        if (!bid.HasValue && !ask.HasValue && _executionAdapter != null)
        {
            var t = _executionAdapter.GetCurrentMarketPrice(ExecutionInstrument, utcNow);
            bid = t.Bid;
            ask = t.Ask;
        }

        var tolerance = toleranceTicks * _tickSize;
        var valid = IsBreakoutStillValidForEntry(bid, ask, brkLong.Value, brkShort.Value, toleranceTicks, failClosedOnMissingQuotes);
        var longInvalid = ask.HasValue && ask.Value >= brkLong.Value + tolerance;
        var shortInvalid = bid.HasValue && bid.Value <= brkShort.Value - tolerance;
        var missingQuotes = !bid.HasValue && !ask.HasValue;

        if (valid)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "BREAKOUT_VALIDATED_UNIFIED", State.ToString(),
                new
                {
                    stream_id = Stream,
                    path,
                    current_bid = bid,
                    current_ask = ask,
                    brk_long = brkLong,
                    brk_short = brkShort,
                    tolerance_ticks = toleranceTicks,
                    note = "Unified breakout entry validity — proceed"
                }));
            return true;
        }

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "BREAKOUT_INVALIDATED_UNIFIED", State.ToString(),
            new
            {
                stream_id = Stream,
                path,
                current_bid = bid,
                current_ask = ask,
                brk_long = brkLong,
                brk_short = brkShort,
                tolerance_ticks = toleranceTicks,
                long_invalid = longInvalid,
                short_invalid = shortInvalid,
                missing_quotes = missingQuotes,
                fail_closed_on_missing_quotes = failClosedOnMissingQuotes,
                reason = missingQuotes && failClosedOnMissingQuotes ? "missing_quotes_fail_closed" : "beyond_breakout_tolerance",
                note = "Unified breakout entry validity — block"
            }));
        return false;
    }

    /// <summary>
    /// Phase B: Execute pending recovery action. Runs pre-execution gate, then resubmit or cancel-rebuild.
    /// </summary>
    public bool ExecutePendingRecoveryAction(AccountSnapshot snap, DateTimeOffset utcNow)
    {
        if (_entryOrderRecoveryState.Action == EntryOrderRecoveryAction.None)
            return false;
        if (Committed || State != StreamState.RANGE_LOCKED || _journal.ExecutionInterruptedByClose || _entryDetected || utcNow >= MarketCloseUtc)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "ENTRY_ORDER_ACTION_CLEARED_STREAM_INELIGIBLE", State.ToString(),
                new { stream_id = Stream, reason = "stream_no_longer_eligible" }));
            ClearRecoveryAction(utcNow);
            return false;
        }
        if (IsPostLockBreakoutSetupExpired())
        {
            ClearRecoveryAction(utcNow);
            _ = Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED");
            return false;
        }
        var posQty = snap.Positions?.Where(p => IsSameInstrument(p.Instrument)).Sum(p => p.Quantity) ?? 0;
        if (posQty != 0)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "ENTRY_ORDER_SET_RESUBMIT_SKIPPED_POSITION_EXISTS", State.ToString(),
                new { stream_id = Stream, position_qty = posQty }));
            ClearRecoveryAction(utcNow);
            return false;
        }
        if (HasValidEntryOrdersOnBroker(snap))
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "ENTRY_ORDER_SET_RESUBMIT_SKIPPED_VALID_EXISTS", State.ToString(),
                new { stream_id = Stream }));
            ClearRecoveryAction(utcNow);
            return false;
        }

        if (_entryOrderRecoveryState.Action == EntryOrderRecoveryAction.CancelAndRebuild)
        {
            var orderIds = GetEntryOrderIdsForStream(snap);
            if (orderIds.Count > 0)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDER_SET_CANCEL_REQUESTED", State.ToString(),
                    new { stream_id = Stream, order_ids = orderIds }));
                _executionAdapter?.CancelOrders(orderIds, utcNow);
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDER_SET_REBUILD_BLOCKED_CANCEL_INCOMPLETE", State.ToString(),
                    new { stream_id = Stream, note = "Cancel sent; will retry rebuild on next cycle after confirmation" }));
                return true; // Don't clear - wait for next cycle when orders are gone
            }
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "ENTRY_ORDER_SET_REBUILD_REQUESTED", State.ToString(),
                new { stream_id = Stream }));
            _entryOrderRecoveryState = new EntryOrderRecoveryState { Action = EntryOrderRecoveryAction.ResubmitClean, Reason = _entryOrderRecoveryState.Reason + "_after_cancel", IssuedUtc = utcNow };
            _journal.RecoveryAction = "ResubmitClean";
            _journal.RecoveryActionReason = _entryOrderRecoveryState.Reason;
            _journal.RecoveryActionIssuedUtc = utcNow.ToString("o");
            _journals.Save(_journal);
        }
        if (_entryOrderRecoveryState.Action == EntryOrderRecoveryAction.ResubmitClean)
        {
            SubmitStopEntryBracketsAtLock(utcNow);
            if (_stopBracketsSubmittedAtLock)
            {
                ClearRecoveryAction(utcNow);
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDERS_RESUBMITTED", State.ToString(),
                    new { stream_id = Stream, reason = "recovery_resubmit" }));
            }
            return true;
        }
        return false;
    }

    public void Arm(DateTimeOffset utcNow)
    {
        if (_journal.Committed)
        {
            State = StreamState.DONE;
            return;
        }

        // CRITICAL: If range was restored from logs, skip normal flow
        // Restoration happens in constructor before Arm() is called
        // If _rangeLocked == true, we should already be in RANGE_LOCKED state
        if (_rangeLocked)
        {
            // Range was restored - verify state is correct
            if (State != StreamState.RANGE_LOCKED)
            {
                LogHealth("ERROR", "RANGE_LOCKED_STATE_MISMATCH",
                    "Range lock restored but state is not RANGE_LOCKED",
                    new
                    {
                        range_locked = _rangeLocked,
                        current_state = State.ToString(),
                        note = "Restoration should have set state to RANGE_LOCKED"
                    });
                // Force transition to correct state
                Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED_RESTORED_FIX");
            }
            EnsureCommittedForPostLockExcursion(utcNow);
            // Skip normal flow - restoration already completed
            return;
        }

        // A) Strategy lifecycle: New slot / stream armed
        LogHealth("INFO", "STREAM_ARMED", $"Stream armed for slot {SlotTimeChicago}",
            new { slot_time_chicago = SlotTimeChicago, instrument = Instrument, session = Session });

        // Reset pre-hydration and gap tracking state on re-arming
        // SIM mode: Uses NinjaTrader historical bars (buffered in OnBar during PRE_HYDRATION)
        // DRYRUN mode: Uses file-based pre-hydration
        _preHydrationComplete = false;

        _largestSingleGapMinutes = 0.0;
        _totalGapMinutes = 0.0;
        _lastBarOpenChicago = null;
        _rangeInvalidated = false;
        _rangeInvalidatedNotified = false; // Reset notification flag on new slot
        _slotEndSummaryLogged = false;
        _lastHeartbeatUtc = null;
        _lastBarReceivedUtc = null;
        _lastBarTimestampUtc = null;
        _lastPreHydrationHandlerTraceUtc = null;

        // Streams start in PRE_HYDRATION for both SIM and DRYRUN
        // SIM mode: Uses NinjaTrader historical bars (buffered in OnBar)
        // DRYRUN mode: Uses file-based pre-hydration
        Transition(utcNow, StreamState.PRE_HYDRATION, "STREAM_ARMED");
    }

    public void EnterRecoveryManage(DateTimeOffset utcNow, string reason)
    {
        if (_journal.Committed)
        {
            State = StreamState.DONE;
            return;
        }

        // Reset range lock flags for explicit recovery restart
        _rangeLocked = false;
        _rangeLockCommitted = false;
        _rangeLockAttemptedAtUtc = null;
        _rangeLockFailureCount = 0;
        _breakoutLevelsMissing = false;

        // Transition directly to DONE instead of RECOVERY_MANAGE
        if (!Commit(utcNow, "STREAM_STAND_DOWN", "STREAM_STAND_DOWN")) return;
    }

    private DateTimeOffset _lastExecutionGateEvalBarUtc = DateTimeOffset.MinValue;
    private const int EXECUTION_GATE_EVAL_RATE_LIMIT_SECONDS = 60; // Log once per minute max

    public void OnBar(DateTimeOffset barUtc, decimal open, decimal high, decimal low, decimal close, DateTimeOffset utcNow, bool isHistorical = false)
    {
        // CRITICAL: Convert bar timestamp to Chicago time explicitly
        // Do NOT assume barUtc is UTC or Chicago - conversion must be explicit
        var barChicagoTime = _time.ConvertUtcToChicago(barUtc);

        // SAFETY CHECK: Filter bars by trading date (RobotEngine already filters, but this is defensive)
        var barTradingDate = _time.GetChicagoDateToday(barUtc).ToString("yyyy-MM-dd");
        if (barTradingDate != TradingDate)
        {
            // Bar is from wrong trading date - ignore it
            // This should not happen (RobotEngine filters first), but this is a defensive check
            return;
        }

        // Early return if stream is committed (optimization - avoid unnecessary processing)
        if (_journal.Committed)
        {
            return;
        }

        // Liveness Fix: Decouple bar buffering from stream state
        // Bars within range window [RangeStartChicagoTime, SlotTimeChicagoTime) must always be buffered,
        // regardless of stream state. State should only gate decisions (e.g., range computation, execution),
        // not data ingestion. Bar timestamps represent bar end time for range window comparisons.

        // DIAGNOSTIC: Log bar reception + admission proof (only if diagnostic logs enabled, rate-limited).
        // BAR_ADMISSION_PROOF was previously unconditional and dominated log volume during busy windows.
        bool shouldLogBar = false;
        if (_enableDiagnosticLogs)
        {
            shouldLogBar = !_lastBarDiagnosticTime.HasValue ||
                          (utcNow - _lastBarDiagnosticTime.Value).TotalSeconds >= _barDiagnosticRateLimitSeconds;

            if (shouldLogBar)
            {
                _lastBarDiagnosticTime = utcNow;
                var inRange = barChicagoTime >= RangeStartChicagoTime && barChicagoTime <= SlotTimeChicagoTime;
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BAR_RECEIVED_DIAGNOSTIC", State.ToString(),
                    new
                    {
                        bar_utc = barUtc.ToString("o"),
                        bar_utc_kind = barUtc.DateTime.Kind.ToString(),
                        bar_chicago = barChicagoTime.ToString("o"),
                        bar_chicago_offset = barChicagoTime.Offset.ToString(),
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        range_end_chicago = SlotTimeChicagoTime.ToString("o"),
                        range_start_utc = RangeStartUtc.ToString("o"),
                        range_end_utc = SlotTimeUtc.ToString("o"),
                        in_range_window = inRange,
                        bar_buffer_count = _barBuffer.Count,
                        time_until_slot_seconds = (SlotTimeUtc - utcNow).TotalSeconds
                    }));

                var barSourceStr = isHistorical ? "BARSREQUEST" : "LIVE";
                var comparisonResult = barChicagoTime >= RangeStartChicagoTime && barChicagoTime <= SlotTimeChicagoTime;
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BAR_ADMISSION_PROOF", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        canonical_instrument = CanonicalInstrument,
                        instrument = Instrument,
                        bar_time_raw_utc = barUtc.ToString("o"),
                        bar_time_raw_kind = barUtc.DateTime.Kind.ToString(),
                        bar_time_chicago = barChicagoTime.ToString("o"),
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                        comparison_result = comparisonResult,
                        comparison_detail = comparisonResult
                            ? $"bar_chicago ({barChicagoTime:HH:mm:ss}) >= range_start ({RangeStartChicagoTime:HH:mm:ss}) AND bar_chicago <= slot_time ({SlotTimeChicagoTime:HH:mm:ss})"
                            : $"bar_chicago ({barChicagoTime:HH:mm:ss}) NOT in [range_start ({RangeStartChicagoTime:HH:mm:ss}), slot_time ({SlotTimeChicagoTime:HH:mm:ss})]",
                        bar_source = barSourceStr,
                        note = "Diagnostic proof log (gated + rate-limited with BAR_RECEIVED_DIAGNOSTIC)"
                    }));
            }
        }

        // C) Data feed anomaly: Check for out-of-order bars
        if (_lastBarTimestampUtc.HasValue && barUtc < _lastBarTimestampUtc.Value)
        {
            LogHealth("WARN", "DATA_FEED_OUT_OF_ORDER", "Bar received out of chronological order",
                new
                {
                    bar_utc = barUtc.ToString("o"),
                    previous_bar_utc = _lastBarTimestampUtc.Value.ToString("o"),
                    gap_minutes = (barUtc - _lastBarTimestampUtc.Value).TotalMinutes
                });
        }

        // C) Data feed anomaly: Check for bars outside expected session window
        if (barChicagoTime < RangeStartChicagoTime.AddMinutes(-5) || barChicagoTime > SlotTimeChicagoTime.AddMinutes(5))
        {
            LogHealth("WARN", "DATA_FEED_OUTSIDE_WINDOW", "Bar timestamp outside expected session window",
                new
                {
                    bar_chicago = barChicagoTime.ToString("o"),
                    range_start_chicago = RangeStartChicagoTime.ToString("o"),
                    slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                    minutes_before_start = (barChicagoTime - RangeStartChicagoTime).TotalMinutes,
                    minutes_after_end = (barChicagoTime - SlotTimeChicagoTime).TotalMinutes
                });
        }

        // Update last bar received timestamp for data feed health monitoring
        _lastBarReceivedUtc = utcNow;
        _lastBarTimestampUtc = barUtc;

        // Post-slot OHLC envelope (all states): feeds pre-submit breakout scan; journal flags are optional memory.
        if (barUtc > SlotTimeUtc && high >= low && close >= low && close <= high)
        {
            if (!_postSlotExcursionHasSamples)
            {
                _postSlotExcursionHasSamples = true;
                _postSlotMaxHighSinceSlot = high;
                _postSlotMinLowSinceSlot = low;
            }
            else
            {
                if (high > _postSlotMaxHighSinceSlot) _postSlotMaxHighSinceSlot = high;
                if (low < _postSlotMinLowSinceSlot) _postSlotMinLowSinceSlot = low;
            }
        }

        // Buffer bars that fall within [range_start, slot_time] using Chicago time comparison
        // Bar timestamps represent OPEN time (converted from NinjaTrader close time for Analyzer parity)
        // Range window is defined in Chicago time to match trading session semantics
        // State-independent buffering: Always buffer bars within range window regardless of state
        // CRITICAL FIX: Include slot_time bar (<= instead of <) so range lock check runs when slot_time bar arrives

        if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime <= SlotTimeChicagoTime)
        {
                // DEFENSIVE: Validate bar data before buffering
                string? validationError = null;
                if (high < low)
                {
                    validationError = "high < low";
                }
                else if (close < low || close > high)
                {
                    validationError = "close outside [low, high]";
                }

                if (validationError != null)
                {
                    // C) Data feed anomaly: Invalid bar data (WARN level)
                    LogHealth("WARN", "DATA_FEED_INVALID_BAR", $"Invalid bar data: {validationError}",
                        new
                        {
                            bar_utc = barUtc.ToString("o"),
                            bar_chicago = barChicagoTime.ToString("o"),
                            high = high,
                            low = low,
                            close = close,
                            validation_error = validationError
                        });

                    // Log invalid bar but continue (fail-closed per bar, not per stream)
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_INVALID", State.ToString(),
                        new
                        {
                            instrument = Instrument,
                            bar_utc_time = barUtc.ToString("o"),
                            bar_chicago_time = barChicagoTime.ToString("o"),
                            high = high,
                            low = low,
                            close = close,
                            reason = validationError
                        }));
                    // Skip invalid bar - do not add to buffer
                    return;
                }

                // RANGE_FIRST_BAR_ACCEPTED: Emit once per stream per day when first bar enters range
                if (!_firstBarAcceptedAssertEmitted)
                {
                    _firstBarAcceptedAssertEmitted = true;
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "RANGE_FIRST_BAR_ACCEPTED", State.ToString(),
                        new
                        {
                            bar_utc_time = barUtc.ToString("o"),
                            bar_chicago_time = barChicagoTime.ToString("o"),
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            comparison_result = "bar >= range_start",
                            note = "first accepted bar"
                        }));
                }

                // Log if buffering in unexpected state (rate-limited)
                var isExpectedState = State == StreamState.PRE_HYDRATION || State == StreamState.ARMED || State == StreamState.RANGE_BUILDING;

                // BINARY TRUTH EVENT: Prove admission-to-commit decision point
                var commitReason = isExpectedState ? "COMMIT_ALLOWED" : $"STATE_GUARD_{State}";
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BAR_ADMISSION_TO_COMMIT_DECISION", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        state = State.ToString(),
                        admitted = true,  // bar passed admission check (we're here)
                        will_commit = isExpectedState,  // state allows buffering
                        reason = commitReason,
                        bar_time_chicago = barChicagoTime.ToString("o"),
                        range_start = RangeStartChicagoTime.ToString("o"),
                        slot_time = SlotTimeChicagoTime.ToString("o")
                    }));

                if (!isExpectedState)
                {
                    // Rate-limit warning to once per stream per 5 minutes
                    var shouldLogWarning = !_lastBarBufferedStateIndependentUtc.HasValue ||
                        (utcNow - _lastBarBufferedStateIndependentUtc.Value).TotalMinutes >= 5.0;

                    if (shouldLogWarning)
                    {
                        _lastBarBufferedStateIndependentUtc = utcNow;
                        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "BAR_BUFFERED_STATE_INDEPENDENT", State.ToString(),
                            new
                            {
                                stream_state = State.ToString(),
                                bar_count = GetBarBufferCount(),
                                bar_chicago = barChicagoTime.ToString("o"),
                                note = "Bar buffered in unexpected state - state-independent buffering active"
                            }));
                    }
                }

                // Add bar to buffer with actual open price
                // Determine bar source: LIVE if from live feed, BARSREQUEST if marked as historical from BarsRequest
                var barSource = isHistorical ? BarSource.BARSREQUEST : BarSource.LIVE;
                AddBarToBuffer(new Bar(barUtc, open, high, low, close, null), barSource);

                // Gap tolerance tracking (treat bar timestamp as bar OPEN time in Chicago)
                if (_lastBarOpenChicago.HasValue)
                {
                    // IMPORTANT:
                    // - For 1-minute bars, a "normal" delta is ~1 minute.
                    // - A delta of 2 minutes means we are missing ~1 minute-bar (2 - 1).
                    // We track missing minutes (delta - 1), not the raw delta, otherwise totals explode too fast.
                    var gapDeltaMinutes = (barChicagoTime - _lastBarOpenChicago.Value).TotalMinutes;

                    // Only track gaps > 1 minute (normal 1-minute bars have ~1 minute gaps)
                    if (gapDeltaMinutes > 1.0)
                    {
                        var missingMinutes = gapDeltaMinutes - 1.0;

                        // Update gap tracking
                        if (missingMinutes > _largestSingleGapMinutes)
                            _largestSingleGapMinutes = missingMinutes;

                        var totalGapBefore = _totalGapMinutes;
                        _totalGapMinutes += missingMinutes;
                        var totalGapAfter = _totalGapMinutes;
                        var largestGapAfter = _largestSingleGapMinutes;

                        // BAR_GAP_DETECTED: Diagnostic event to instantly identify gap root causes
                        // This helps determine if gaps are real missing minutes, time mapping errors,
                        // out-of-order bars, or filtering issues
                        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                            "BAR_GAP_DETECTED", State.ToString(),
                            new
                            {
                                stream_id = Stream,
                                prev_bar_open_chicago = _lastBarOpenChicago.Value.ToString("o"),
                                this_bar_open_chicago = barChicagoTime.ToString("o"),
                                delta_minutes = gapDeltaMinutes,
                                added_to_total_gap = missingMinutes,
                                total_gap_now = totalGapAfter,
                                largest_gap_now = largestGapAfter,
                                bar_source = barSource.ToString(),
                                // Additional diagnostic context
                                stream_state = State.ToString(),
                                bar_timestamp_utc = barUtc.ToString("o"),
                                gap_type_preliminary = State == StreamState.PRE_HYDRATION || barSource == BarSource.BARSREQUEST ? "DATA_FEED_FAILURE" : "LOW_LIQUIDITY",
                                note = "Gap detected between consecutive bars. Check prev_bar_open_chicago vs this_bar_open_chicago to identify root cause."
                            }));

                        // Classify gap type: DATA_FEED_FAILURE vs LOW_LIQUIDITY
                        // DATA_FEED_FAILURE indicators:
                        // - Gaps during PRE_HYDRATION (BARSREQUEST should return complete data)
                        // - Very low bar count overall (suggests data feed issue)
                        // - Gaps from BARSREQUEST source (historical data should be complete)
                        // LOW_LIQUIDITY indicators:
                        // - Gaps during RANGE_BUILDING from LIVE feed (market genuinely sparse)
                        // - Reasonable bar count but with gaps (some trading occurred)
                        var totalBarCount = _historicalBarCount + _liveBarCount;
                        var expectedMinBars = (int)((SlotTimeChicagoTime - RangeStartChicagoTime).TotalMinutes * 0.5); // Expect at least 50% coverage
                        var isDataFeedFailure =
                            State == StreamState.PRE_HYDRATION || // PRE_HYDRATION gaps = data feed issue
                            barSource == BarSource.BARSREQUEST || // Historical gaps = data feed issue
                            totalBarCount < expectedMinBars; // Very low bar count = data feed issue

                        var gapType = isDataFeedFailure ? "DATA_FEED_FAILURE" : "LOW_LIQUIDITY";
                        var gapTypeNote = isDataFeedFailure
                            ? "Gap likely due to data feed failure (PRE_HYDRATION/BARSREQUEST gaps or insufficient data)"
                            : "Gap likely due to legitimate low liquidity (sparse trading during live feed)";

                        // Check gap tolerance rules - DISABLED: DATA_FEED_FAILURE gaps no longer invalidate
                        // Both DATA_FEED_FAILURE and LOW_LIQUIDITY gaps are now tolerated (never invalidate)
                        bool violated = false;
                        string violationReason = "";

                        // TEMPORARILY DISABLED: DATA_FEED_FAILURE gap invalidation
                        // Previously, DATA_FEED_FAILURE gaps would invalidate ranges, but this is now disabled
                        // All gaps (both DATA_FEED_FAILURE and LOW_LIQUIDITY) are tolerated and logged for monitoring
                        // if (isDataFeedFailure)
                        // {
                        //     if (missingMinutes > MAX_SINGLE_GAP_MINUTES)
                        //     {
                        //         violated = true;
                        //         violationReason = $"Single gap missing {missingMinutes:F1} minutes exceeds MAX_SINGLE_GAP_MINUTES ({MAX_SINGLE_GAP_MINUTES}) for DATA_FEED_FAILURE";
                        //     }
                        //     else if (_totalGapMinutes > MAX_TOTAL_GAP_MINUTES)
                        //     {
                        //         violated = true;
                        //         violationReason = $"Total gap missing {_totalGapMinutes:F1} minutes exceeds MAX_TOTAL_GAP_MINUTES ({MAX_TOTAL_GAP_MINUTES}) for DATA_FEED_FAILURE";
                        //     }
                        //
                        //     // Check last 10 minutes rule
                        //     var last10MinStart = SlotTimeChicagoTime.AddMinutes(-10);
                        //     if (barChicagoTime >= last10MinStart && missingMinutes > MAX_GAP_LAST_10_MINUTES)
                        //     {
                        //         violated = true;
                        //         violationReason = $"Gap missing {missingMinutes:F1} minutes in last 10 minutes exceeds MAX_GAP_LAST_10_MINUTES ({MAX_GAP_LAST_10_MINUTES}) for DATA_FEED_FAILURE";
                        //     }
                        // }
                        // LOW_LIQUIDITY gaps: Never invalidate (violated stays false)
                        // DATA_FEED_FAILURE gaps: Also never invalidate (violated stays false) - TEMPORARILY DISABLED

                        if (violated)
                        {
                            var wasInvalidated = _rangeInvalidated;
                            _rangeInvalidated = true;

                            var gapViolationData = new
                            {
                                instrument = Instrument,  // Canonical (top-level for backward compatibility)
                                execution_instrument = ExecutionInstrument,  // PHASE 3: Execution identity
                                canonical_instrument = CanonicalInstrument,   // PHASE 3: Canonical identity
                                slot = Stream,
                                violation_reason = violationReason,
                                gap_type = gapType,
                                gap_type_note = gapTypeNote,
                                // Backward-compat: keep gap_minutes, but now it means "missing minutes"
                                gap_minutes = missingMinutes,
                                // Extra forensic context: raw delta between bar opens
                                gap_delta_minutes = gapDeltaMinutes,
                                largest_single_gap_minutes = _largestSingleGapMinutes,
                                total_gap_minutes = _totalGapMinutes,
                                previous_bar_open_chicago = _lastBarOpenChicago.Value.ToString("o"),
                                current_bar_open_chicago = barChicagoTime.ToString("o"),
                                slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                                gap_location = $"Between {_lastBarOpenChicago.Value:HH:mm} and {barChicagoTime:HH:mm} Chicago time",
                                minutes_until_slot_time = (SlotTimeChicagoTime - barChicagoTime).TotalMinutes,
                                // Diagnostic context for gap classification
                                stream_state = State.ToString(),
                                bar_source = barSource.ToString(),
                                total_bar_count = totalBarCount,
                                historical_bar_count = _historicalBarCount,
                                live_bar_count = _liveBarCount,
                                expected_min_bars = expectedMinBars,
                                range_start_chicago = RangeStartChicagoTime.ToString("o"),
                                note = isDataFeedFailure
                                    ? (missingMinutes > MAX_SINGLE_GAP_MINUTES
                                        ? $"Single gap missing {missingMinutes:F1} minutes exceeds limit of {MAX_SINGLE_GAP_MINUTES} minutes for DATA_FEED_FAILURE"
                                        : _totalGapMinutes > MAX_TOTAL_GAP_MINUTES
                                        ? $"Total gaps missing {_totalGapMinutes:F1} minutes exceed limit of {MAX_TOTAL_GAP_MINUTES} minutes for DATA_FEED_FAILURE"
                                        : $"Gap missing {missingMinutes:F1} minutes in last 10 minutes exceeds limit of {MAX_GAP_LAST_10_MINUTES} minutes for DATA_FEED_FAILURE")
                                    : $"LOW_LIQUIDITY gap tolerated (gaps from low liquidity never invalidate range)"
                            };

                            // Log to health directory (detailed health tracking)
                            LogHealth("ERROR", "GAP_TOLERANCE_VIOLATION", $"Range invalidated due to gap violation: {violationReason}", gapViolationData);

                            // Also log to main engine log for easier discovery
                            _log.Write(RobotEvents.Base(_time, DateTimeOffset.UtcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                                "GAP_TOLERANCE_VIOLATION", State.ToString(), gapViolationData));

                            // Notify RANGE_INVALIDATED once per slot (transition false → true)
                            if (!wasInvalidated && !_rangeInvalidatedNotified)
                            {
                                _rangeInvalidatedNotified = true;
                                var notificationKey = $"RANGE_INVALIDATED:{Stream}";
                                var title = $"Range Invalidated: {Instrument} {Stream}";
                                var message = $"Range invalidated due to gap violation: {violationReason}. " +
                                           $"Gap type: {gapType} - {gapTypeNote}. Trading blocked for this slot.";
                                _alertCallback?.Invoke(notificationKey, title, message, 1); // High priority
                            }
                        }
                        else
                        {
                            // Gap within tolerance - keep diagnostic detail without inflating warning counts.
                            LogHealth("INFO", "GAP_TOLERATED", $"Gap missing {missingMinutes:F1} minutes tolerated (within limits for {gapType})",
                                new
                                {
                                    instrument = Instrument,
                                    slot = Stream,
                                    gap_type = gapType,
                                    gap_type_note = gapTypeNote,
                                    gap_minutes = missingMinutes,
                                    gap_delta_minutes = gapDeltaMinutes,
                                    largest_single_gap_minutes = _largestSingleGapMinutes,
                                    total_gap_minutes = _totalGapMinutes,
                                    previous_bar_open_chicago = _lastBarOpenChicago.Value.ToString("o"),
                                    current_bar_open_chicago = barChicagoTime.ToString("o"),
                                    stream_state = State.ToString(),
                                    bar_source = barSource.ToString()
                                });
                        }
                    }
                }

                // Update last bar open time (Chicago, bar OPEN time)
                _lastBarOpenChicago = barChicagoTime;

                // SPECULATIVE UPDATE — MUST NOT RUN AFTER RANGE_LOCKED
                // Keep incremental updates only while State == RANGE_BUILDING AND _rangeLocked == false
                // After _rangeLocked == true, OnBar must not modify RangeHigh/RangeLow/FreezeClose/FreezeCloseSource
                // This is non-negotiable. Without it, late bars can mutate a locked range.
                if (State == StreamState.RANGE_BUILDING && !_rangeLocked)
                {
                    // INCREMENTAL UPDATE: Update RangeHigh/RangeLow as bars arrive
                    // This allows range to update in real-time instead of only at slot_time
                    if (RangeHigh == null || high > RangeHigh.Value)
                        RangeHigh = high;
                    if (RangeLow == null || low < RangeLow.Value)
                        RangeLow = low;
                    // Always update FreezeClose to latest bar's close (will be last bar before slot_time)
                    FreezeClose = close;
                    FreezeCloseSource = "BAR_CLOSE";
                    // Persist snapshot for restart recovery (whenever range or bar count changes)
                    PersistRangeBuildingSnapshot(barUtc);
                }
        }
        else
        {
            // DIAGNOSTIC: Log bars that are filtered out (rate-limited, only when close to window and diagnostics enabled)
            if (_enableDiagnosticLogs && shouldLogBar)
            {
                var timeUntilStart = (RangeStartChicagoTime - barChicagoTime).TotalMinutes;
                var timeAfterEnd = (barChicagoTime - SlotTimeChicagoTime).TotalMinutes;
                if (Math.Abs(timeUntilStart) < 30 || Math.Abs(timeAfterEnd) < 30)
                {
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "BAR_FILTERED_OUT", State.ToString(),
                        new
                        {
                            bar_utc = barUtc.ToString("o"),
                            bar_chicago = barChicagoTime.ToString("o"),
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            range_end_chicago = SlotTimeChicagoTime.ToString("o"),
                            reason = barChicagoTime < RangeStartChicagoTime ? "BEFORE_RANGE_START" : "AFTER_RANGE_END",
                            minutes_from_start = timeUntilStart,
                            minutes_from_end = timeAfterEnd
                        }));
                }
            }
        }
        // Bars at/after slot time are for breakout detection (handled in RANGE_LOCKED state)

        // Handle RANGE_LOCKED state separately (bars at/after slot time for breakout detection)
        if (State == StreamState.RANGE_LOCKED)
        {
            ApplyPostLockBreakoutExcursionFromBar(high, low, barUtc, utcNow);
            if (_journal.Committed)
                return;

            // DIAGNOSTIC: Log execution gate evaluation (rate-limited, only if diagnostics enabled)
            if (_enableDiagnosticLogs)
            {
                var barChicago = _time.ConvertUtcToChicago(barUtc);
                var timeSinceLastEval = (barUtc - _lastExecutionGateEvalBarUtc).TotalSeconds;
                if (timeSinceLastEval >= EXECUTION_GATE_EVAL_RATE_LIMIT_SECONDS || _lastExecutionGateEvalBarUtc == DateTimeOffset.MinValue)
                {
                    _lastExecutionGateEvalBarUtc = barUtc;
                    LogExecutionGateEval(barUtc, barChicago, utcNow);
                }
            }

            // SIMPLIFICATION: Removed CheckBreakoutEntry() - stop brackets handle breakouts automatically
            // Stop brackets (OCO-linked Long + Short) are submitted at lock and fill automatically when price hits breakout level
            // This eliminates race conditions and simplifies execution to 2 paths: immediate entry OR stop brackets
            // Breakout detection on bars is no longer needed - stop brackets handle it
            //
            // OLD CODE (removed):
            // if (!_entryDetected && barUtc >= SlotTimeUtc && barUtc < MarketCloseUtc && _brkLongRounded.HasValue && _brkShortRounded.HasValue)
            // {
            //     CheckBreakoutEntry(barUtc, high, low, utcNow);
            // }
        }
    }

    /// <summary>
    /// Diagnostic: Log execution gate evaluation to identify which gate is blocking execution.
    /// </summary>
    private void LogExecutionGateEval(DateTimeOffset barUtc, DateTimeOffset barChicago, DateTimeOffset utcNow)
    {
        var barChicagoTime = barChicago.ToString("HH:mm:ss");
        var slotTimeUtcParsed = SlotTimeUtc;
        var slotReached = barUtc >= slotTimeUtcParsed;
        var slotTimeChicagoStr = SlotTimeChicago ?? "";

        // Evaluate all gates
        var realtimeOk = true; // Assume realtime if we're getting bars
        var tradingDay = TradingDate ?? "";
        var session = Session ?? "";
        var sessionActive = !string.IsNullOrEmpty(session) && _spec.sessions.ContainsKey(session);
        var timetableEnabled = true; // Timetable is validated at engine level
        var streamArmed = !_journal.Committed && State != StreamState.DONE;
        var stateOk = State == StreamState.RANGE_LOCKED;
        var entryDetectionModeOk = true; // FIXED: Now works for all modes

        // Check if we can detect entries
        var canDetectEntries = stateOk && !_entryDetected && slotReached &&
                               barUtc < MarketCloseUtc &&
                               _brkLongRounded.HasValue && _brkShortRounded.HasValue;

        // Final allowed: all gates must pass (execution mode is adapter-only, not a gate)
        var finalAllowed = realtimeOk &&
                          !string.IsNullOrEmpty(tradingDay) &&
                          sessionActive &&
                          slotReached &&
                          timetableEnabled &&
                          streamArmed &&
                          stateOk &&
                          entryDetectionModeOk &&
                          canDetectEntries;

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "EXECUTION_GATE_EVAL", State.ToString(),
            new
            {
                bar_timestamp_chicago = barChicagoTime,
                bar_timestamp_utc = barUtc.ToString("o"),
                slot_time_chicago = slotTimeChicagoStr,
                slot_time_utc = slotTimeUtcParsed.ToString("o"),
                realtime_ok = realtimeOk,
                trading_day = tradingDay,
                session = session,
                session_active = sessionActive,
                slot_reached = slotReached,
                timetable_enabled = timetableEnabled,
                stream_armed = streamArmed,
                state_ok = stateOk,
                state = State.ToString(),
                entry_detection_mode_ok = entryDetectionModeOk,
                execution_mode = _executionMode.ToString(),
                can_detect_entries = canDetectEntries,
                entry_detected = _entryDetected,
                breakout_levels_computed = _brkLongRounded.HasValue && _brkShortRounded.HasValue,
                final_allowed = finalAllowed
            }));

        // INVARIANT CHECK: If slot time has passed and execution should be allowed but isn't, log ERROR
        // Only trigger violation if execution is blocked for UNEXPECTED reasons (not legitimate blocks)
        // Estimate bar interval (typically 1 minute for NG)
        var estimatedBarIntervalMinutes = 1;
        var barInterval = TimeSpan.FromMinutes(estimatedBarIntervalMinutes);
        var slotTimePlusInterval = slotTimeUtcParsed.Add(barInterval);

        // Determine which gates are failing
        var failedGates = new List<string>();
        if (!realtimeOk) failedGates.Add("REALTIME_OK");
        if (string.IsNullOrEmpty(tradingDay)) failedGates.Add("TRADING_DAY_SET");
        if (!sessionActive) failedGates.Add("SESSION_ACTIVE");
        if (!slotReached) failedGates.Add("SLOT_REACHED");
        if (!timetableEnabled) failedGates.Add("TIMETABLE_ENABLED");
        if (!streamArmed) failedGates.Add("STREAM_ARMED");
        if (!stateOk) failedGates.Add("STATE_OK");
        if (!entryDetectionModeOk) failedGates.Add("ENTRY_DETECTION_MODE_OK");
        if (!canDetectEntries) failedGates.Add("CAN_DETECT_ENTRIES");

        // Only violate if execution is blocked for UNEXPECTED reasons:
        // - State is OK (RANGE_LOCKED)
        // - Slot has been reached
        // - Stream is armed (not committed/done)
        // - Entry not detected yet (still waiting for entry)
        // - Breakout levels are computed (ready to detect entries)
        // This excludes legitimate blocks: entry already detected, journal committed, breakout levels not ready
        var unexpectedBlock = barUtc >= slotTimePlusInterval &&
                             !finalAllowed &&
                             stateOk &&
                             slotReached &&
                             streamArmed &&  // Stream should be armed
                             !_entryDetected &&  // Entry not detected yet
                             _brkLongRounded.HasValue && _brkShortRounded.HasValue;  // Breakout levels ready

        if (unexpectedBlock)
        {
            // This is a real violation - execution should be allowed but isn't
            var payload = new Dictionary<string, object>
            {
                ["error"] = "EXECUTION_SHOULD_BE_ALLOWED_BUT_IS_NOT",
                ["bar_timestamp_chicago"] = barChicagoTime,
                ["slot_time_chicago"] = slotTimeChicagoStr,
                ["slot_time_utc"] = slotTimeUtcParsed.ToString("o"),
                ["bar_interval_minutes"] = estimatedBarIntervalMinutes,
                ["realtime_ok"] = realtimeOk,
                ["trading_day"] = tradingDay,
                ["session_active"] = sessionActive,
                ["slot_reached"] = slotReached,
                ["timetable_enabled"] = timetableEnabled,
                ["stream_armed"] = streamArmed,
                ["can_detect_entries"] = canDetectEntries,
                ["entry_detected"] = _entryDetected,
                ["breakout_levels_computed"] = _brkLongRounded.HasValue && _brkShortRounded.HasValue,
                ["execution_mode"] = _executionMode.ToString(),
                ["instrument"] = Instrument,
                ["stream"] = Stream,
                ["trading_date"] = TradingDate,
                ["state"] = State.ToString(),
                ["failed_gates"] = string.Join(", ", failedGates),  // List of gates that failed
                ["message"] = $"Slot time has passed but execution is not allowed. Failed gates: {string.Join(", ", failedGates)}"
            };

            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "EXECUTION_GATE_INVARIANT_VIOLATION", State.ToString(),
                payload));

            // Report critical event to HealthMonitor for notification
            _reportCriticalCallback?.Invoke("EXECUTION_GATE_INVARIANT_VIOLATION", payload, TradingDate);
        }
    }

    private void ComputeBreakoutLevelsAndLog(DateTimeOffset utcNow)
    {
        if (RangeHigh is null || RangeLow is null)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "BREAKOUT_LEVELS_COMPUTED", State.ToString(),
                new { error = "MISSING_RANGE_VALUES", rounding_required = true }));
            return;
        }

        // Compute raw breakout levels
        _brkLongRaw = RangeHigh.Value + _tickSize;
        _brkShortRaw = RangeLow.Value - _tickSize;

        // Round using Analyzer-equivalent method (ALL execution modes need rounded levels)
        _brkLongRounded = UtilityRoundToTick.RoundToTick(_brkLongRaw.Value, _tickSize);
        _brkShortRounded = UtilityRoundToTick.RoundToTick(_brkShortRaw.Value, _tickSize);

        // Log breakout levels (all modes - was DRYRUN-only, now unconditional for consistency)
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "BREAKOUT_LEVELS_COMPUTED", State.ToString(),
            new
            {
                brk_long_raw = _brkLongRaw,
                brk_short_raw = _brkShortRaw,
                brk_long_rounded = _brkLongRounded,
                brk_short_rounded = _brkShortRounded,
                tick_size = _tickSize,
                rounding_method = _spec.breakout.tick_rounding.method
            }));
    }

    private void LogIntendedBracketsPlaced(DateTimeOffset utcNow)
    {
        BracketsIntended = true;
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "INTENDED_BRACKETS_PLACED", State.ToString(),
            new { brackets_intended = true, note = "Brackets intended. In SIM/LIVE, stop-entry brackets may be submitted at RANGE_LOCKED." }));
    }

    /// <summary>
    /// Consolidated precondition check for stop bracket submission.
    /// Returns a tuple indicating whether submission can proceed and the reason if not.
    /// </summary>
    private (bool CanSubmit, string Reason, object? Details) CanSubmitStopBrackets(DateTimeOffset utcNow)
    {
        // Idempotency: only once per stream per day
        if (_stopBracketsSubmittedAtLock)
        {
            return (false, "IDEMPOTENCY", new { _stopBracketsSubmittedAtLock = true });
        }

        // Preconditions
        if (_journal.Committed || State == StreamState.DONE)
        {
            return (false, "JOURNAL_COMMITTED_OR_DONE", new { journal_committed = _journal?.Committed ?? false, state = State.ToString() });
        }

        if (_rangeInvalidated)
        {
            return (false, "RANGE_INVALIDATED", new { _rangeInvalidated = true });
        }

        if (_breakoutLevelsMissing)
        {
            return (false, "BREAKOUT_LEVELS_MISSING", new { _breakoutLevelsMissing = true, note = "Stream gated from entry until breakout levels are computed" });
        }

        if (_executionAdapter == null || _executionJournal == null || _riskGate == null)
        {
            return (false, "NULL_DEPENDENCIES", new { execution_adapter_null = _executionAdapter == null, execution_journal_null = _executionJournal == null, risk_gate_null = _riskGate == null });
        }

        if (!_executionAdapter.IsExecutionContextReady)
        {
            return (false, "EXECUTION_CONTEXT_NOT_READY", new
            {
                note = "NT context not wired or SIM not verified — bracket submission deferred (startup/readiness contract)"
            });
        }

        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
        {
            return (false, "BREAKOUT_LEVELS_MISSING", new { brk_long_has_value = _brkLongRounded.HasValue, brk_short_has_value = _brkShortRounded.HasValue });
        }

        if (!RangeHigh.HasValue || !RangeLow.HasValue)
        {
            return (false, "RANGE_VALUES_MISSING", new { range_high_has_value = RangeHigh.HasValue, range_low_has_value = RangeLow.HasValue });
        }

        if (IsPostLockBreakoutSetupExpired())
        {
            return (false, "POST_LOCK_BREAKOUT_ALREADY_OCCURRED", new
            {
                post_lock_long = _journal.PostLockLongBreakoutTouched,
                post_lock_short = _journal.PostLockShortBreakoutTouched,
                breakout_source = "JOURNAL_FLAGS"
            });
        }

        var (ohlcL, ohlcS) = GetPostSlotBreakoutTouchFromOhlcEnvelope(_brkLongRounded.Value, _brkShortRounded.Value);
        if (ohlcL || ohlcS)
        {
            return (false, "POST_LOCK_BREAKOUT_ALREADY_OCCURRED", new
            {
                post_lock_long = ohlcL || _journal.PostLockLongBreakoutTouched,
                post_lock_short = ohlcS || _journal.PostLockShortBreakoutTouched,
                ohlc_long = ohlcL,
                ohlc_short = ohlcS,
                max_high_since_slot = _postSlotExcursionHasSamples ? _postSlotMaxHighSinceSlot : (decimal?)null,
                min_low_since_slot = _postSlotExcursionHasSamples ? _postSlotMinLowSinceSlot : (decimal?)null,
                breakout_source = "OHLC_ENVELOPE"
            });
        }

        return (true, "OK", null);
    }

    /// <summary>
    /// True after RANGE_LOCK if bar path shows either breakout was touched before entry brackets were armed.
    /// Strict product rule: touching either side expires the entire setup (no stop-entry brackets on either side).
    /// </summary>
    public bool IsPostLockBreakoutSetupExpired()
        => _journal.PostLockLongBreakoutTouched || _journal.PostLockShortBreakoutTouched;

    /// <summary>
    /// Primary signal for &quot;breakout already occurred&quot; before first bracket submit: tracked OHLC strictly after SlotTimeUtc.
    /// </summary>
    private (bool longTouched, bool shortTouched) GetPostSlotBreakoutTouchFromOhlcEnvelope(decimal brkLong, decimal brkShort)
    {
        if (!_postSlotExcursionHasSamples) return (false, false);
        return (_postSlotMaxHighSinceSlot >= brkLong, _postSlotMinLowSinceSlot <= brkShort);
    }

    /// <summary>
    /// If OHLC since slot or persisted journal shows post-lock excursion, commit NO_TRADE and return true.
    /// Call before initial SubmitStopEntryBracketsAtLock from TryLockRange Phase B.
    /// </summary>
    private bool TryCommitNoTradeIfPostLockBreakoutDetected(DateTimeOffset utcNow, string detectionPath)
    {
        if (_journal.Committed) return false;
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue) return false;

        var brkL = _brkLongRounded.Value;
        var brkS = _brkShortRounded.Value;
        var (ohlcL, ohlcS) = GetPostSlotBreakoutTouchFromOhlcEnvelope(brkL, brkS);
        var journalL = _journal.PostLockLongBreakoutTouched;
        var journalS = _journal.PostLockShortBreakoutTouched;
        if (!ohlcL && !ohlcS && !journalL && !journalS)
            return false;

        if (ohlcL) _journal.PostLockLongBreakoutTouched = true;
        if (ohlcS) _journal.PostLockShortBreakoutTouched = true;
        _journals.Save(_journal);

        var source = (ohlcL || ohlcS) && (journalL || journalS)
            ? "OHLC_AND_JOURNAL"
            : (ohlcL || ohlcS ? "OHLC_ENVELOPE" : "JOURNAL_FLAGS_ONLY");

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "INITIAL_SUBMISSION_BLOCKED_POST_LOCK_EXCURSION", State.ToString(),
            new
            {
                stream_id = Stream,
                trading_date = TradingDate,
                detection_path = detectionPath,
                source,
                ohlc_long = ohlcL,
                ohlc_short = ohlcS,
                post_lock_long = _journal.PostLockLongBreakoutTouched,
                post_lock_short = _journal.PostLockShortBreakoutTouched,
                max_high_since_slot = _postSlotExcursionHasSamples ? _postSlotMaxHighSinceSlot : (decimal?)null,
                min_low_since_slot = _postSlotExcursionHasSamples ? _postSlotMinLowSinceSlot : (decimal?)null,
                breakout_long = brkL,
                breakout_short = brkS,
                note = "Post-slot OHLC envelope or journal memory — no stop-entry brackets (strict product rule)"
            }));

        _ = Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED");
        return true;
    }

    private void SyncJournalPostLockFlagsFromOhlcEnvelopeIfTouched()
    {
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue) return;
        var (l, s) = GetPostSlotBreakoutTouchFromOhlcEnvelope(_brkLongRounded.Value, _brkShortRounded.Value);
        if (!l && !s) return;
        if (l) _journal.PostLockLongBreakoutTouched = true;
        if (s) _journal.PostLockShortBreakoutTouched = true;
        _journals.Save(_journal);
    }

    private static bool IsEntryStopMarketMovedRejection(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        return error.IndexOf("can't be placed", StringComparison.OrdinalIgnoreCase) >= 0 ||
               error.IndexOf("cannot be placed", StringComparison.OrdinalIgnoreCase) >= 0 ||
               error.IndexOf("price outside limits", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void MarkPostLockBreakoutFromRejectedSide(bool longRejected, bool shortRejected)
    {
        if (longRejected) _journal.PostLockLongBreakoutTouched = true;
        if (shortRejected) _journal.PostLockShortBreakoutTouched = true;
        _journals.Save(_journal);
    }

    /// <summary>
    /// Deterministic post-lock excursion: bar must be strictly after slot end; uses bar high/low vs rounded breakout stops.
    /// On first touch, persists journal flags, emits audit event, and commits NO_TRADE_BREAKOUT_ALREADY_OCCURRED.
    /// </summary>
    private void ApplyPostLockBreakoutExcursionFromBar(decimal high, decimal low, DateTimeOffset barUtc, DateTimeOffset utcNow)
    {
        if (!_rangeLocked || State != StreamState.RANGE_LOCKED) return;
        if (_journal.Committed) return;
        if (_stopBracketsSubmittedAtLock || _journal.StopBracketsSubmittedAtLock) return;
        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue) return;
        if (barUtc <= SlotTimeUtc) return;

        var brkL = _brkLongRounded.Value;
        var brkS = _brkShortRounded.Value;
        var longTouch = high >= brkL;
        var shortTouch = low <= brkS;
        if (!longTouch && !shortTouch) return;

        var priorL = _journal.PostLockLongBreakoutTouched;
        var priorS = _journal.PostLockShortBreakoutTouched;
        if (longTouch) _journal.PostLockLongBreakoutTouched = true;
        if (shortTouch) _journal.PostLockShortBreakoutTouched = true;
        _journals.Save(_journal);

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "ENTRY_INVALIDATED_POST_LOCK_EXCURSION", State.ToString(),
            new
            {
                stream_id = Stream,
                trading_date = TradingDate,
                bar_utc = barUtc.ToString("o"),
                breakout_long = brkL,
                breakout_short = brkS,
                bar_high = high,
                bar_low = low,
                long_touched = longTouch || priorL,
                short_touched = shortTouch || priorS,
                note = "Breakout touched on post-lock bar path before entry brackets armed — setup expired (strict)"
            }));

        if (!Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED")) return;
    }

    /// <summary>
    /// Restart safety: journal persisted excursion flags but commit did not complete (e.g. crash). Finish NO_TRADE.
    /// </summary>
    private void EnsureCommittedForPostLockExcursion(DateTimeOffset utcNow)
    {
        if (_journal.Committed || State != StreamState.RANGE_LOCKED) return;
        if (!IsPostLockBreakoutSetupExpired()) return;
        if (!Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED")) return;
    }

    private DerivedPositionAuthority TryDerivedPositionAuthorityForStream(DateTimeOffset utcNow)
    {
        if (_executionAdapter == null || _executionJournal == null)
            return DerivedPositionAuthority.UNKNOWN;
        try
        {
            var snap = _executionAdapter.GetAccountSnapshot(utcNow);
            return PositionAuthorityInstrumentEvaluator.Derive(snap, _executionJournal, ExecutionInstrument, CanonicalInstrument);
        }
        catch
        {
            return DerivedPositionAuthority.UNKNOWN;
        }
    }

    private DateTimeOffset ResolveDeferredBracketExpiryUtc(DateTimeOffset utcNow)
    {
        if (_journal.NextSlotTimeUtc.HasValue && _journal.NextSlotTimeUtc.Value > utcNow)
            return _journal.NextSlotTimeUtc.Value;
        if (MarketCloseUtc > utcNow)
            return MarketCloseUtc;
        return utcNow.AddMinutes(15);
    }

    private void ClearDeferredBracketTrade()
    {
        _deferredBracketTradePending = false;
        _deferredBracketTradeExpiryUtc = null;
        _loggedTradeExecutedFromDeferred = false;
    }

    private void TryProcessDeferredBracketTradeAuthority(DateTimeOffset utcNow)
    {
        if (!_deferredBracketTradePending) return;
        if (State != StreamState.RANGE_LOCKED || _journal.Committed)
        {
            ClearDeferredBracketTrade();
            return;
        }

        if (_deferredBracketTradeExpiryUtc.HasValue && utcNow > _deferredBracketTradeExpiryUtc.Value)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "TRADE_EXPIRED", State.ToString(),
                new
                {
                    stream_id = Stream,
                    trading_date = TradingDate,
                    execution_instrument = ExecutionInstrument,
                    authority_deferred_expiry_utc = _deferredBracketTradeExpiryUtc.Value.ToString("o"),
                    note = "Deferred stop-entry bracket submit expired before REAL authority"
                }));
            ClearDeferredBracketTrade();
            return;
        }

        var auth = TryDerivedPositionAuthorityForStream(utcNow);
        if (auth == DerivedPositionAuthority.UNKNOWN)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "TRADE_CANCELLED_UNKNOWN_STATE", State.ToString(),
                new
                {
                    stream_id = Stream,
                    trading_date = TradingDate,
                    execution_instrument = ExecutionInstrument,
                    authority_state = auth.ToString(),
                    reason = "authority_unknown",
                    note = "Deferred bracket submit cancelled — position authority UNKNOWN"
                }));
            ClearDeferredBracketTrade();
            return;
        }

        if (auth != DerivedPositionAuthority.REAL)
            return;

        if (!_loggedTradeExecutedFromDeferred)
        {
            _loggedTradeExecutedFromDeferred = true;
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "TRADE_EXECUTED_FROM_DEFERRED", State.ToString(),
                new
                {
                    stream_id = Stream,
                    trading_date = TradingDate,
                    execution_instrument = ExecutionInstrument,
                    authority_state = auth.ToString(),
                    note = "Executing deferred stop-entry brackets now that authority is REAL"
                }));
        }

        SubmitStopEntryBracketsAtLock(utcNow, fromDeferredAuthorityExecution: true);
        if (_stopBracketsSubmittedAtLock || _journal.StopBracketsSubmittedAtLock)
            ClearDeferredBracketTrade();
    }

    /// <summary>
    /// Submit paired stop-market entry orders (long + short) immediately after RANGE_LOCKED.
    /// These are linked via OCO so only one side can fill.
    /// </summary>
    private void SubmitStopEntryBracketsAtLock(DateTimeOffset utcNow, bool fromDeferredAuthorityExecution = false)
    {
        // DIAGNOSTIC: Log entry into function
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "STOP_BRACKETS_SUBMIT_ENTERED", State.ToString(),
            new
            {
                stream_id = Stream,
                trading_date = TradingDate,
                _stopBracketsSubmittedAtLock = _stopBracketsSubmittedAtLock,
                journal_committed = _journal?.Committed ?? false,
                state = State.ToString(),
                range_invalidated = _rangeInvalidated,
                execution_adapter_null = _executionAdapter == null,
                execution_journal_null = _executionJournal == null,
                risk_gate_null = _riskGate == null,
                brk_long_has_value = _brkLongRounded.HasValue,
                brk_short_has_value = _brkShortRounded.HasValue,
                range_high_has_value = RangeHigh.HasValue,
                range_low_has_value = RangeLow.HasValue,
                note = "Entered SubmitStopEntryBracketsAtLock - checking preconditions"
            }));

        // SIMPLIFICATION: Use consolidated precondition check
        var canSubmitResult = CanSubmitStopBrackets(utcNow);
        if (!canSubmitResult.CanSubmit)
        {
            if (canSubmitResult.Reason == "POST_LOCK_BREAKOUT_ALREADY_OCCURRED")
            {
                SyncJournalPostLockFlagsFromOhlcEnvelopeIfTouched();
                _ = Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED");
            }
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_EARLY_RETURN", State.ToString(),
                new { reason = canSubmitResult.Reason, details = canSubmitResult.Details }));
            return;
        }

        // Risk gate (fail-closed)
        bool allowed = false;
        string? reason = null;
        List<string>? failedGates = null;
        var streamArmed = !_journal.Committed && State != StreamState.DONE;

        try
        {
            var gateResult = _riskGate.CheckGates(
                _executionMode,
                TradingDate,
                Stream,
                Instrument,
                Session,
                SlotTimeChicago,
                timetableValidated: true,
                streamArmed: streamArmed,
                utcNow);
            allowed = gateResult.Allowed;
            reason = gateResult.Reason;
            failedGates = gateResult.FailedGates;
        }
        catch (Exception ex)
        {
            // CRITICAL: Catch exceptions from risk gate check to prevent crashes
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_EARLY_RETURN", State.ToString(),
                new
                {
                    reason = "RISK_GATE_CHECK_EXCEPTION",
                    exception_type = ex.GetType().Name,
                    exception_message = ex.Message,
                    stack_trace = ex.StackTrace,
                    note = "Risk gate check threw exception - blocking order submission to prevent crash"
                }));
            return;
        }

        if (!allowed)
        {
            // Use a deterministic id for bracket attempt logs (not a trade intent id)
            var gateIntentId = $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}";
            try
            {
                _riskGate.LogBlocked(gateIntentId, Instrument, Stream, Session, SlotTimeChicago, TradingDate,
                    reason ?? "UNKNOWN", failedGates ?? new List<string>(), streamArmed, true, utcNow);
            }
            catch (Exception ex)
            {
                // Log that LogBlocked failed but continue
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "STOP_BRACKETS_LOG_BLOCKED_FAILED", State.ToString(),
                    new
                    {
                        exception_type = ex.GetType().Name,
                        exception_message = ex.Message,
                        note = "RiskGate.LogBlocked() threw exception - continuing with early return log"
                    }));
            }

            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_EARLY_RETURN", State.ToString(),
                new
                {
                    reason = "RISK_GATE_BLOCKED",
                    risk_gate_reason = reason ?? "UNKNOWN",
                    failed_gates = failedGates ?? new List<string>(),
                    stream_armed = streamArmed
                }));
            return;
        }

        if (!fromDeferredAuthorityExecution)
        {
            var posAuth = TryDerivedPositionAuthorityForStream(utcNow);
            _log.Write(RobotEvents.EngineBase(utcNow, TradingDate, "POSITION_AUTHORITY_EVALUATED", "ENGINE",
                new
                {
                    stream_id = Stream,
                    instrument = ExecutionInstrument,
                    canonical_instrument = CanonicalInstrument,
                    authority_state = posAuth.ToString(),
                    context = "timetable_stop_brackets_at_lock"
                }));
            if (posAuth == DerivedPositionAuthority.UNKNOWN)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "TRADE_BLOCKED_UNKNOWN_STATE", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        trading_date = TradingDate,
                        execution_instrument = ExecutionInstrument,
                        authority_state = posAuth.ToString(),
                        blocked_what = "STOP_ENTRY_BRACKETS",
                        reason = "authority_unknown",
                        note = "No pending trade created — position authority UNKNOWN"
                    }));
                return;
            }

            if (posAuth == DerivedPositionAuthority.RECOVERY)
            {
                if (_deferredBracketTradePending)
                    return;
                _deferredBracketTradePending = true;
                _deferredBracketTradeExpiryUtc = ResolveDeferredBracketExpiryUtc(utcNow);
                _loggedTradeExecutedFromDeferred = false;
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "TRADE_DEFERRED_POSITION_AUTHORITY", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        trading_date = TradingDate,
                        execution_instrument = ExecutionInstrument,
                        authority_state = posAuth.ToString(),
                        deferred_expiry_utc = _deferredBracketTradeExpiryUtc?.ToString("o"),
                        blocked_what = "STOP_ENTRY_BRACKETS",
                        note = "Bracket submit deferred until authority is REAL"
                    }));
                return;
            }
        }

        var brkLong = _brkLongRounded.Value;
        var brkShort = _brkShortRounded.Value;

        // CRITICAL FIX: OCO ID must be unique - NinjaTrader doesn't allow reusing OCO IDs
        // If same stream locks multiple times, previous OCO ID would be reused, causing rejection
        // Add unique identifier (GUID) to ensure each OCO group is unique
        // Shared OCO group links the two entry stops
        var ocoGroup = RobotOrderIds.EncodeEntryOco(TradingDate, Stream, SlotTimeChicago);

        // Compute protective prices deterministically from lock snapshot (pure computation)
        var rh = RangeHigh.Value;
        var rl = RangeLow.Value;
        var (longStop, longTarget, longBeTrigger) = ComputeProtectivesFromLockSnapshot("Long", brkLong, rh, rl);
        var (shortStop, shortTarget, shortBeTrigger) = ComputeProtectivesFromLockSnapshot("Short", brkShort, rh, rl);

        // Build intents (canonical) for idempotency + journaling
        var longIntent = new Intent(
            TradingDate,
            Stream,
            Instrument,
            ExecutionInstrument,
            Session,
            SlotTimeChicago,
            "Long",
            brkLong,
            stopPrice: longStop,
            targetPrice: longTarget,
            beTrigger: longBeTrigger,
            entryTimeUtc: utcNow,
            triggerReason: "ENTRY_STOP_BRACKET_LONG");

        var shortIntent = new Intent(
            TradingDate,
            Stream,
            Instrument,
            ExecutionInstrument,
            Session,
            SlotTimeChicago,
            "Short",
            brkShort,
            stopPrice: shortStop,
            targetPrice: shortTarget,
            beTrigger: shortBeTrigger,
            entryTimeUtc: utcNow,
            triggerReason: "ENTRY_STOP_BRACKET_SHORT");

        var longIntentId = longIntent.ComputeIntentId();
        var shortIntentId = shortIntent.ComputeIntentId();

        // Already submitted? then treat as done. Bypass when recovery action requires resubmit (broker/journal diverge).
        if (!_entryOrderRecoveryState.IsPending && _executionJournal != null &&
            (_executionJournal.IsIntentSubmitted(longIntentId, TradingDate, Stream) ||
             _executionJournal.IsIntentSubmitted(shortIntentId, TradingDate, Stream)))
        {
            _stopBracketsSubmittedAtLock = true;
            _journal.StopBracketsSubmittedAtLock = true; // PERSIST: Update journal
            _journals.Save(_journal);
            return;
        }

        // CRITICAL: Position check before resubmit - prevent double exposure.
        // Reconciliation may have run when flat, but a fill could have occurred before this tick.
        if (_entryOrderRecoveryState.IsPending && _executionAdapter != null)
        {
            try
            {
                var snap = _executionAdapter.GetAccountSnapshot(utcNow);
                var posQty = snap.Positions?.Where(p => IsSameInstrument(p.Instrument)).Sum(p => p.Quantity) ?? 0;
                if (posQty != 0)
                {
                    ClearRecoveryAction(utcNow);
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "ENTRY_ORDERS_RESUBMIT_BLOCKED_POSITION_NOT_FLAT", State.ToString(),
                        new
                        {
                            stream_id = Stream,
                            trading_date = TradingDate,
                            position_qty = posQty,
                            reason = "double_exposure_prevention",
                            note = "Resubmit blocked - position not flat; would create double exposure"
                        }));
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_ORDERS_RESUBMIT_POSITION_CHECK_ERROR", State.ToString(),
                    new { error = ex.Message, note = "Position check failed - blocking resubmit for safety" }));
                return;
            }
        }

        try
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}", Instrument, "STOP_BRACKETS_SUBMIT_ATTEMPT", new
            {
                stream_id = Stream,
                trading_date = TradingDate,
                slot_time_chicago = SlotTimeChicago,
                brk_long = brkLong,
                brk_short = brkShort,
                oco_group = ocoGroup,
                long_stop_price = longStop,
                long_target_price = longTarget,
                long_be_trigger = longBeTrigger,
                short_stop_price = shortStop,
                short_target_price = shortTarget,
                short_be_trigger = shortBeTrigger,
                note = "Submitting paired stop-market entry orders at RANGE_LOCKED"
            }));

            // CRITICAL: Register intents BEFORE order submission so protective orders can be placed on fill
            // This MUST happen before SubmitStopEntryOrder() is called
            if (_executionAdapter is NinjaTraderSimAdapter ntAdapter)
            {
                ntAdapter.RegisterIntent(longIntent);
                ntAdapter.RegisterIntent(shortIntent);

                // CRITICAL FIX: Register policy expectations BEFORE order submission
                // This is required for pre-submission validation checks
                ntAdapter.RegisterIntentPolicy(longIntentId, _orderQuantity, _maxQuantity,
                    CanonicalInstrument, ExecutionInstrument, "EXECUTION_POLICY_FILE");
                ntAdapter.RegisterIntentPolicy(shortIntentId, _orderQuantity, _maxQuantity,
                    CanonicalInstrument, ExecutionInstrument, "EXECUTION_POLICY_FILE");
            }
            else
            {
                // CRITICAL ERROR: Execution adapter is not NinjaTraderSimAdapter - intents cannot be registered
                // This will cause protective orders to fail on fill
                _log.Write(RobotEvents.ExecutionBase(utcNow, $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}", Instrument, "EXECUTION_ERROR",
                    new
                    {
                        error = "Execution adapter is not NinjaTraderSimAdapter - RegisterIntent() cannot be called",
                        execution_adapter_type = _executionAdapter?.GetType().Name ?? "NULL",
                        long_intent_id = longIntentId,
                        short_intent_id = shortIntentId,
                        note = "CRITICAL: Protective orders will NOT be placed on fill because intents are not registered"
                    }));
            }

            // Pre-submit: a crossed breakout stop is either converted to MARKET within tolerance or rejected as missed.
            var (bid, ask) = _executionAdapter.GetCurrentMarketPrice(ExecutionInstrument, utcNow);
            var toleranceTicks = GetBreakoutValidityToleranceTicks();
            var longSubmit = ResolveBreakoutEntrySubmitAction("Long", brkLong, bid, ask, toleranceTicks);
            var shortSubmit = ResolveBreakoutEntrySubmitAction("Short", brkShort, bid, ask, toleranceTicks);
            var longDecision = longSubmit.Action.ToString().ToUpperInvariant();
            var shortDecision = shortSubmit.Action.ToString().ToUpperInvariant();

            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "ENTRY_ORDER_TYPE_DECISION", State.ToString(),
                new { stream = Stream, side = "LONG", decision = longDecision, bid, ask, brk_long = brkLong, brk_short = brkShort, crossed_distance_ticks = longSubmit.DistanceTicks, tolerance_ticks = toleranceTicks, reason = longSubmit.Reason }));
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "ENTRY_ORDER_TYPE_DECISION", State.ToString(),
                new { stream = Stream, side = "SHORT", decision = shortDecision, bid, ask, brk_long = brkLong, brk_short = brkShort, crossed_distance_ticks = shortSubmit.DistanceTicks, tolerance_ticks = toleranceTicks, reason = shortSubmit.Reason }));

            if (longSubmit.Action == BreakoutEntrySubmitAction.Reject ||
                shortSubmit.Action == BreakoutEntrySubmitAction.Reject)
            {
                MarkPostLockBreakoutFromRejectedSide(
                    longSubmit.Action == BreakoutEntrySubmitAction.Reject,
                    shortSubmit.Action == BreakoutEntrySubmitAction.Reject);
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "ENTRY_BREAKOUT_TOO_FAR_REJECTED", State.ToString(),
                    new
                    {
                        stream = Stream,
                        bid,
                        ask,
                        brk_long = brkLong,
                        brk_short = brkShort,
                        long_decision = longDecision,
                        short_decision = shortDecision,
                        long_crossed_distance_ticks = longSubmit.DistanceTicks,
                        short_crossed_distance_ticks = shortSubmit.DistanceTicks,
                        tolerance_ticks = toleranceTicks,
                        note = "Breakout price was crossed beyond tolerance before bracket submission; treating as missed opportunity."
                    }));
                LogSlotEndSummary(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", false, false,
                    "Range locked; breakout crossed beyond tolerance before entry could be submitted");
                _ = Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED");
                return;
            }

            OrderSubmissionResult longRes;
            OrderSubmissionResult shortRes;
            if (longSubmit.Action == BreakoutEntrySubmitAction.Market)
                longRes = _executionAdapter.SubmitEntryOrder(longIntentId, ExecutionInstrument, "Long", null, _orderQuantity, "MARKET", ocoGroup, utcNow);
            else
                longRes = _executionAdapter.SubmitStopEntryOrder(longIntentId, ExecutionInstrument, "Long", brkLong, _orderQuantity, ocoGroup, utcNow);
            if (shortSubmit.Action == BreakoutEntrySubmitAction.Market)
                shortRes = _executionAdapter.SubmitEntryOrder(shortIntentId, ExecutionInstrument, "Short", null, _orderQuantity, "MARKET", ocoGroup, utcNow);
            else
                shortRes = _executionAdapter.SubmitStopEntryOrder(shortIntentId, ExecutionInstrument, "Short", brkShort, _orderQuantity, ocoGroup, utcNow);

            // Persist to execution journal for idempotency (record both attempts)
            // PHASE 2: Journal uses ExecutionInstrument for execution tracking
            if (longRes.Success)
                _executionJournal.RecordSubmission(
                    longIntentId,
                    TradingDate,
                    Stream,
                    ExecutionInstrument,
                    "ENTRY_STOP_LONG",
                    longRes.BrokerOrderId,
                    utcNow,
                    expectedEntryPrice: brkLong,
                    entryPrice: brkLong,
                    stopPrice: longStop,
                    targetPrice: longTarget,
                    beTriggerPrice: longBeTrigger,
                    direction: "Long",
                    ocoGroup: ocoGroup);
            else
            {
                var longErr = longRes.ErrorMessage ?? "ENTRY_STOP_LONG_FAILED";
                _executionJournal.RecordRejection(longIntentId, TradingDate, Stream, longErr, utcNow);
                if (longErr.IndexOf("price outside limits", StringComparison.OrdinalIgnoreCase) >= 0)
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "ENTRY_ORDER_REJECTED_PRICE_INVALID", State.ToString(),
                        new { stream = Stream, side = "LONG", submitted_price = brkLong, current_bid = bid, current_ask = ask }));
            }

            if (shortRes.Success)
                _executionJournal.RecordSubmission(
                    shortIntentId,
                    TradingDate,
                    Stream,
                    ExecutionInstrument,
                    "ENTRY_STOP_SHORT",
                    shortRes.BrokerOrderId,
                    utcNow,
                    expectedEntryPrice: brkShort,
                    entryPrice: brkShort,
                    stopPrice: shortStop,
                    targetPrice: shortTarget,
                    beTriggerPrice: shortBeTrigger,
                    direction: "Short",
                    ocoGroup: ocoGroup);
            else
            {
                var shortErr = shortRes.ErrorMessage ?? "ENTRY_STOP_SHORT_FAILED";
                _executionJournal.RecordRejection(shortIntentId, TradingDate, Stream, shortErr, utcNow);
                if (shortErr.IndexOf("price outside limits", StringComparison.OrdinalIgnoreCase) >= 0)
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "ENTRY_ORDER_REJECTED_PRICE_INVALID", State.ToString(),
                        new { stream = Stream, side = "SHORT", submitted_price = brkShort, current_bid = bid, current_ask = ask }));
            }

            if (longRes.Success && shortRes.Success)
            {
                _stopBracketsSubmittedAtLock = true;

                // PERSIST: Save flag to journal immediately
                _journal.StopBracketsSubmittedAtLock = true;
                _journals.Save(_journal);

                if (_entryOrderRecoveryState.IsPending)
                {
                    ClearRecoveryAction(utcNow);
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "ENTRY_ORDERS_RESUBMITTED", State.ToString(),
                        new
                        {
                            stream_id = Stream,
                            trading_date = TradingDate,
                            long_intent_id = longIntentId,
                            short_intent_id = shortIntentId,
                            long_broker_order_id = longRes.BrokerOrderId,
                            short_broker_order_id = shortRes.BrokerOrderId,
                            reason = "reconciliation_resubmit",
                            note = "Entry orders resubmitted after reconciliation detected missing/invalid orders"
                        }));
                }

                _log.Write(RobotEvents.ExecutionBase(utcNow, $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}", Instrument, "STOP_BRACKETS_SUBMITTED", new
                {
                    stream_id = Stream,
                    trading_date = TradingDate,
                    slot_time_chicago = SlotTimeChicago,
                    long_intent_id = longIntentId,
                    short_intent_id = shortIntentId,
                    long_broker_order_id = longRes.BrokerOrderId,
                    short_broker_order_id = shortRes.BrokerOrderId,
                    oco_group = ocoGroup,
                    persisted_to_journal = true,
                    note = "Stop entry brackets submitted; breakout fill should not require additional submission"
                }));

                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BRACKET_SUBMIT_OUTCOME", State.ToString(),
                    new
                    {
                        outcome = "submitted_both",
                        stream_id = Stream,
                        trading_date = TradingDate,
                        slot_time_chicago = SlotTimeChicago,
                        note = "Post-submit truth: both stop entry brackets accepted by adapter"
                    }));
                LogSlotEndSummary(utcNow, "RANGE_VALID", true, false, "Range locked; stop brackets submitted, awaiting fill");
            }
            else
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}", Instrument, "STOP_BRACKETS_SUBMIT_FAILED", new
                {
                    stream_id = Stream,
                    trading_date = TradingDate,
                    slot_time_chicago = SlotTimeChicago,
                    oco_group = ocoGroup,
                    long_success = longRes.Success,
                    long_error = longRes.ErrorMessage,
                    short_success = shortRes.Success,
                    short_error = shortRes.ErrorMessage,
                    note = "Failed to submit one or both stop entry brackets"
                }));
                var bothRejected = !longRes.Success && !shortRes.Success;
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BRACKET_SUBMIT_OUTCOME", State.ToString(),
                    new
                    {
                        outcome = bothRejected ? "rejected_both" : "rejected_partial",
                        stream_id = Stream,
                        trading_date = TradingDate,
                        slot_time_chicago = SlotTimeChicago,
                        long_success = longRes.Success,
                        short_success = shortRes.Success,
                        note = "Post-submit truth: stop entry bracket submission did not succeed for both sides"
                    }));
                LogSlotEndSummary(utcNow, "RANGE_VALID", true, false,
                    bothRejected
                        ? "Range locked; stop bracket submission failed (both sides) — no working entry orders"
                        : "Range locked; stop bracket submission incomplete (one side) — review adapter errors");
                var partialRejected = longRes.Success != shortRes.Success;
                if (partialRejected && _executionAdapter is NinjaTraderSimAdapter partialCancelAdapter)
                {
                    try
                    {
                        if (longRes.Success) partialCancelAdapter.CancelIntentOrders(longIntentId, utcNow);
                        if (shortRes.Success) partialCancelAdapter.CancelIntentOrders(shortIntentId, utcNow);
                        _log.Write(RobotEvents.ExecutionBase(utcNow, $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}", Instrument, "STOP_BRACKETS_PARTIAL_CANCELLED", new
                        {
                            stream_id = Stream,
                            trading_date = TradingDate,
                            long_intent_id = longIntentId,
                            short_intent_id = shortIntentId,
                            long_success = longRes.Success,
                            short_success = shortRes.Success,
                            note = "Paired entry brackets are all-or-none; accepted side was cancelled after sibling rejection."
                        }));
                    }
                    catch (Exception cancelEx)
                    {
                        _log.Write(RobotEvents.ExecutionBase(utcNow, $"BRACKETS_AT_LOCK:{TradingDate}:{Stream}", Instrument, "STOP_BRACKETS_PARTIAL_CANCEL_FAILED", new
                        {
                            stream_id = Stream,
                            trading_date = TradingDate,
                            error = cancelEx.Message,
                            note = "Failed to cancel accepted side after partial entry-bracket rejection."
                        }));
                    }
                }

                var marketMovedLong = !longRes.Success && IsEntryStopMarketMovedRejection(longRes.ErrorMessage);
                var marketMovedShort = !shortRes.Success && IsEntryStopMarketMovedRejection(shortRes.ErrorMessage);
                if (marketMovedLong || marketMovedShort)
                {
                    MarkPostLockBreakoutFromRejectedSide(marketMovedLong, marketMovedShort);
                    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                        "ENTRY_INVALIDATED_POST_LOCK_EXCURSION", State.ToString(),
                        new
                        {
                            stream_id = Stream,
                            trading_date = TradingDate,
                            breakout_long = brkLong,
                            breakout_short = brkShort,
                            long_touched = _journal.PostLockLongBreakoutTouched,
                            short_touched = _journal.PostLockShortBreakoutTouched,
                            long_error = longRes.ErrorMessage,
                            short_error = shortRes.ErrorMessage,
                            source = "BROKER_REJECTED_MARKET_MOVED",
                            note = "Broker rejected one stop-entry side as already marketable; strict setup expired."
                        }));
                    _ = Commit(utcNow, "NO_TRADE_BREAKOUT_ALREADY_OCCURRED", "NO_TRADE_BREAKOUT_ALREADY_OCCURRED");
                }
                else if (bothRejected || partialRejected)
                {
                    // Terminal NO_TRADE: avoid substring FAILED in commit reason (classified as FAILED_RUNTIME elsewhere)
                    _ = Commit(utcNow, "NO_TRADE_ENTRY_BRACKETS_AT_LOCK_REJECTED", "NO_TRADE_ENTRY_BRACKETS_AT_LOCK_REJECTED");
                }
            }
        }
        catch (Exception ex)
        {
            // CRITICAL: Catch all exceptions to prevent crashes
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STOP_BRACKETS_SUBMIT_EXCEPTION", State.ToString(),
                new
                {
                    exception_type = ex.GetType().Name,
                    exception_message = ex.Message,
                    stack_trace = ex.StackTrace,
                    stream_id = Stream,
                    trading_date = TradingDate,
                    brk_long = brkLong,
                    brk_short = brkShort,
                    long_intent_id = longIntentId,
                    short_intent_id = shortIntentId,
                    note = "Exception during stop brackets submission - orders not placed"
                }));
        }
    }
}
