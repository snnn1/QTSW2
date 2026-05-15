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

    private int CountWorkingOrdersForStreamInstrument(AccountSnapshot snap)
    {
        var working = snap.WorkingOrders ?? new List<WorkingOrderSnapshot>();
        var count = 0;
        foreach (var order in working)
        {
            if (IsSameInstrument(order.Instrument))
                count++;
        }
        return count;
    }

    private bool TryDeferForcedFlattenForLivePreEntryBrackets(DateTimeOffset utcNow)
    {
        if (!_stopBracketsSubmittedAtLock && !(_journal.StopBracketsSubmittedAtLock))
            return false;

        if (_executionAdapter == null)
            return false;

        if (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue)
        {
            _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_PRE_ENTRY_BRACKET_AUDIT_DEFERRED", new Dictionary<string, object?>
            {
                ["trading_date"] = TradingDate,
                ["session_class"] = Session,
                ["stream"] = Stream,
                ["instrument"] = ExecutionInstrument,
                ["reason"] = "BREAKOUT_LEVELS_UNAVAILABLE",
                ["note"] = "Pre-entry stream had submitted brackets but breakout levels were unavailable; refusing terminal no-trade commit until order state is known."
            });
            return true;
        }

        AccountSnapshot snap;
        try
        {
            snap = _executionAdapter.GetAccountSnapshot(utcNow);
        }
        catch (Exception ex)
        {
            _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_PRE_ENTRY_BRACKET_AUDIT_DEFERRED", new Dictionary<string, object?>
            {
                ["trading_date"] = TradingDate,
                ["session_class"] = Session,
                ["stream"] = Stream,
                ["instrument"] = ExecutionInstrument,
                ["reason"] = "ACCOUNT_SNAPSHOT_FAILED",
                ["error"] = ex.Message,
                ["note"] = "Cannot prove entry brackets are gone; refusing terminal no-trade commit."
            });
            return true;
        }

        var longIntentId = ComputeIntentId("Long", _brkLongRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_LONG");
        var shortIntentId = ComputeIntentId("Short", _brkShortRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_SHORT");
        var longEntry = LoadExecutionJournalEntryForIntentId(longIntentId, TradingDate);
        var shortEntry = LoadExecutionJournalEntryForIntentId(shortIntentId, TradingDate);
        var pendingJournalEntries = new List<ExecutionJournalEntry>();
        var pendingIntentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddPendingEntry(ExecutionJournalEntry? entry)
        {
            if (!IsPendingUnfilledEntryJournal(entry) || string.IsNullOrWhiteSpace(entry.IntentId))
                return;
            if (pendingIntentIds.Add(entry.IntentId))
                pendingJournalEntries.Add(entry);
        }

        AddPendingEntry(longEntry);
        AddPendingEntry(shortEntry);
        foreach (var streamEntry in LoadExecutionJournalEntriesForStream(TradingDate, Stream))
            AddPendingEntry(streamEntry);

        var journalPendingCount = pendingJournalEntries.Count;
        var instrumentWorkingCount = CountWorkingOrdersForStreamInstrument(snap);
        var (longCount, shortCount, orderIdsRaw) = GetMatchingEntryOrderCounts(snap, longIntentId, shortIntentId);
        var liveEntryOrderCount = longCount + shortCount;
        if (liveEntryOrderCount <= 0 && journalPendingCount <= 0 && instrumentWorkingCount <= 0)
        {
            _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_PRE_ENTRY_BRACKET_AUDIT_EMPTY", new Dictionary<string, object?>
            {
                ["trading_date"] = TradingDate,
                ["session_class"] = Session,
                ["stream"] = Stream,
                ["instrument"] = ExecutionInstrument,
                ["long_intent_id"] = longIntentId,
                ["short_intent_id"] = shortIntentId,
                ["note"] = "Pre-entry forced flatten found no live same-instrument working orders and no pending stream entry journals; terminal no-trade commit can proceed."
            });
            return false;
        }

        if (liveEntryOrderCount <= 0 && journalPendingCount > 0 && instrumentWorkingCount <= 0)
        {
            var terminalized = 0;
            foreach (var pendingEntry in pendingJournalEntries)
            {
                if (!string.IsNullOrWhiteSpace(pendingEntry.IntentId) &&
                    _executionJournal?.RecordCancelledUnfilledEntry(pendingEntry.IntentId, TradingDate, Stream, utcNow) == true)
                    terminalized++;
            }

            _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_PRE_ENTRY_BRACKETS_TERMINALIZED", new Dictionary<string, object?>
            {
                ["trading_date"] = TradingDate,
                ["session_class"] = Session,
                ["stream"] = Stream,
                ["instrument"] = ExecutionInstrument,
                ["journal_pending_count"] = journalPendingCount,
                ["pending_intent_ids"] = pendingJournalEntries.Select(e => e.IntentId).ToList(),
                ["terminalized_count"] = terminalized,
                ["note"] = "Pre-entry journals were pending but broker snapshot showed no same-instrument working orders; marked unfilled entries terminal before no-trade commit."
            });
            return false;
        }

        var orderIds = orderIdsRaw
            .Concat(new[] { longEntry?.BrokerOrderId, shortEntry?.BrokerOrderId })
            .Concat(pendingJournalEntries.Select(e => e.BrokerOrderId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var cancelIntentIds = new List<string>();
        var primaryEntryCancelIntentId = longCount > 0
            ? longIntentId
            : shortCount > 0
                ? shortIntentId
                : longIntentId;
        if (!string.IsNullOrWhiteSpace(primaryEntryCancelIntentId))
            cancelIntentIds.Add(primaryEntryCancelIntentId);
        foreach (var pendingEntry in pendingJournalEntries)
        {
            if (string.IsNullOrWhiteSpace(pendingEntry.IntentId) ||
                string.Equals(pendingEntry.IntentId, longIntentId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pendingEntry.IntentId, shortIntentId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!cancelIntentIds.Contains(pendingEntry.IntentId, StringComparer.OrdinalIgnoreCase))
                cancelIntentIds.Add(pendingEntry.IntentId);
        }

        var shouldCancelNow = !_lastForcedFlattenPreEntryCancelUtc.HasValue ||
                              (utcNow - _lastForcedFlattenPreEntryCancelUtc.Value).TotalSeconds >= 5.0;
        if (shouldCancelNow)
        {
            var cancelAccepted = false;
            try
            {
                if (_executionAdapter is NinjaTraderSimAdapter simAdapter)
                {
                    var routedCancelCount = _executionAdapter.RequestSessionCloseCancelIntents(cancelIntentIds, ExecutionInstrument, utcNow);
                    if (routedCancelCount > 0)
                    {
                        cancelAccepted = true;
                    }
                    else
                    {
                        foreach (var cancelIntentId in cancelIntentIds)
                            cancelAccepted = simAdapter.CancelIntentOrders(cancelIntentId, utcNow) || cancelAccepted;
                    }
                }
                else if (orderIds.Count > 0)
                {
                    _executionAdapter.CancelOrders(orderIds, utcNow);
                    cancelAccepted = true;
                }
            }
            catch (Exception ex)
            {
                _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_PRE_ENTRY_BRACKET_CANCEL_ERROR", new Dictionary<string, object?>
                {
                    ["trading_date"] = TradingDate,
                    ["session_class"] = Session,
                    ["stream"] = Stream,
                    ["instrument"] = ExecutionInstrument,
                    ["error"] = ex.Message,
                    ["live_entry_order_count"] = liveEntryOrderCount
                });
            }

            _lastForcedFlattenPreEntryCancelUtc = utcNow;
            _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_PRE_ENTRY_BRACKETS_CANCEL_REQUESTED", new Dictionary<string, object?>
            {
                ["trading_date"] = TradingDate,
                ["session_class"] = Session,
                ["stream"] = Stream,
                ["instrument"] = ExecutionInstrument,
                ["long_intent_id"] = longIntentId,
                ["short_intent_id"] = shortIntentId,
                ["long_order_count"] = longCount,
                ["short_order_count"] = shortCount,
                ["journal_pending_count"] = journalPendingCount,
                ["pending_intent_ids"] = pendingJournalEntries.Select(e => e.IntentId).ToList(),
                ["instrument_working_count"] = instrumentWorkingCount,
                ["broker_order_ids"] = orderIds,
                ["cancel_intent_ids"] = cancelIntentIds,
                ["entry_oco_cancel_strategy"] = "cancel_one_entry_oco_side_and_allow_broker_oco_sibling_cancel",
                ["cancel_accepted"] = cancelAccepted,
                ["note"] = "Pre-entry forced flatten found live entry brackets; terminal no-trade commit deferred until broker working orders are gone."
            });
        }
        else
        {
            _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_PRE_ENTRY_BRACKETS_CANCEL_PENDING", new Dictionary<string, object?>
            {
                ["trading_date"] = TradingDate,
                ["session_class"] = Session,
                ["stream"] = Stream,
                ["instrument"] = ExecutionInstrument,
                ["live_entry_order_count"] = liveEntryOrderCount,
                ["journal_pending_count"] = journalPendingCount,
                ["instrument_working_count"] = instrumentWorkingCount,
                ["note"] = "Waiting for broker to acknowledge pre-entry bracket cancellation before terminal no-trade commit."
            });
        }

        return true;
    }

    private static bool ShouldDeferSessionCloseBrokerFlatConfirmation()
    {
        return true;
    }

    private bool TryWaitBrokerFlatForExecutionInstrument(
        string executionInstrument,
        DateTimeOffset wallPollDeadlineUtc,
        DateTimeOffset accountSnapshotEventUtc,
        out int remainingSignedQty)
    {
        remainingSignedQty = 0;
        // PLAYBACK_TIME_UNKNOWN: Physical broker polling must elapse in wall-clock time; snapshot uses Tick/event time for consistency with engine.
        while (DateTimeOffset.UtcNow <= wallPollDeadlineUtc)
        {
            try
            {
                var snap = _executionAdapter?.GetAccountSnapshot(accountSnapshotEventUtc);
                remainingSignedQty = snap?.Positions?
                                         .Where(p => string.Equals(p.Instrument, executionInstrument, StringComparison.OrdinalIgnoreCase))
                                         .Sum(p => p.Quantity)
                                     ?? 0;
                if (remainingSignedQty == 0)
                    return true;
            }
            catch
            {
                /* poll until deadline */
            }

            Thread.Sleep(100);
        }

        try
        {
            var snap = _executionAdapter?.GetAccountSnapshot(accountSnapshotEventUtc);
            remainingSignedQty = snap?.Positions?
                                     .Where(p => string.Equals(p.Instrument, executionInstrument, StringComparison.OrdinalIgnoreCase))
                                     .Sum(p => p.Quantity)
                                 ?? 0;
        }
        catch
        {
            remainingSignedQty = int.MaxValue;
        }

        return remainingSignedQty == 0;
    }

    /// <summary>
    /// Session-close forced flatten failure as ENGINE event (robot_ENGINE.jsonl / watchdog feed).
    /// Supplements LogHealth; always includes session_close_forced_flatten for aggregator scoping.
    /// </summary>
    private void LogSessionCloseForcedFlattenFailed(DateTimeOffset utcNow, string failurePhase, string message, Dictionary<string, object?>? extra = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["trading_date"] = TradingDate,
            ["session_class"] = Session,
            ["stream"] = Stream,
            ["instrument"] = ExecutionInstrument,
            ["session_close_forced_flatten"] = true,
            ["failure_phase"] = failurePhase,
            ["message"] = message
        };
        if (extra != null)
        {
            foreach (var kv in extra)
                payload[kv.Key] = kv.Value;
        }
        _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_FAILED", payload);
    }

    private void LogSessionReentryBlocked(DateTimeOffset utcNow, string reasonCode, string detail, Dictionary<string, object?>? extra = null)
    {
        var dedupeKey = $"{TradingDate}|{Stream}|{reasonCode}";
        if (!_sessionReentryBlockedLogKeys.Add(dedupeKey))
            return;
        var payload = new Dictionary<string, object?>
        {
            ["trading_date"] = TradingDate,
            ["session_class"] = Session,
            ["instrument"] = ExecutionInstrument ?? Instrument ?? "",
            ["stream"] = Stream,
            ["reason"] = reasonCode,
            ["detail"] = detail
        };
        if (extra != null)
        {
            foreach (var kv in extra)
                payload[kv.Key] = kv.Value;
        }
        _engine?.LogEngineEvent(utcNow, "SESSION_REENTRY_BLOCKED", payload);
    }

    /// <summary>
    /// Post–broker-confirm exposure check: ENGINE jsonl + watchdog (supplements LogHealth).
    /// </summary>
    private void LogSessionCloseExposureRemainingEngine(DateTimeOffset utcNow, int signedQuantity)
    {
        _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_EXPOSURE_REMAINING", new Dictionary<string, object?>
        {
            ["trading_date"] = TradingDate,
            ["session_class"] = Session,
            ["stream"] = Stream,
            ["instrument"] = ExecutionInstrument,
            ["original_intent_id"] = _journal.OriginalIntentId ?? "",
            ["session_close_forced_flatten"] = true,
            ["quantity"] = signedQuantity,
            ["note"] = "Account snapshot still shows position after FLATTEN_BROKER_FLAT_CONFIRMED path"
        });
    }

    private void LogSessionCloseManualFlattenRequiredEngine(DateTimeOffset utcNow, int signedQuantity)
    {
        _engine?.LogEngineEvent(utcNow, "MANUAL_FLATTEN_REQUIRED", new Dictionary<string, object?>
        {
            ["trading_date"] = TradingDate,
            ["session_class"] = Session,
            ["stream"] = Stream,
            ["instrument"] = ExecutionInstrument,
            ["original_intent_id"] = _journal.OriginalIntentId ?? "",
            ["session_close_forced_flatten"] = true,
            ["quantity"] = signedQuantity,
            ["note"] = "Operator must manually flatten; automated session-close flatten did not clear exposure"
        });
    }
    
    /// <summary>
    /// Handle forced flatten at market close (true pre-close flatten + market-open reentry).
    /// Flattens position immediately, cancels protective orders, persists state for reentry.
    /// CRITICAL: Must NEVER call Commit() or set Committed=true or State=DONE for post-entry case.
    /// </summary>
    public void HandleForcedFlatten(DateTimeOffset utcNow)
    {
        if (_journal.Committed || _journal.SlotStatus != SlotStatus.ACTIVE)
        {
            return; // Already committed or not active
        }
        if (_journal.ExecutionInterruptedByClose)
        {
            return; // Already flattened this session — avoid redundant Flatten() every Tick
        }
        
        // Guard: forced-flatten/reentry applies only to an original entry that is still open.
        // Completed TARGET/STOP trades may leave their stream active until close; those must not be reentered.
        var hasOpenOriginalPosition = false;
        if (!string.IsNullOrWhiteSpace(_journal.OriginalIntentId))
        {
            var originalEntry = LoadExecutionJournalEntryForIntentId(_journal.OriginalIntentId, TradingDate);
            if (originalEntry != null && (originalEntry.EntryFilled || originalEntry.EntryFilledQuantityTotal > 0))
            {
                hasOpenOriginalPosition =
                    !originalEntry.TradeCompleted &&
                    ExecutionJournal.GetEntryRemainingOpenQuantity(originalEntry) > 0;

                if (!hasOpenOriginalPosition)
                {
                    if (TryArmSessionCloseReentryFromCompletedFlatten(utcNow, originalEntry, "original_intent_completed_flatten"))
                        return;

                    if (HasAnyReentryLifecycle())
                    {
                        if (IsReentryLifecycleCompleted())
                        {
                            _ = Commit(utcNow, "REENTRY_TRADE_COMPLETED_MARKET_CLOSE", "TRADE_COMPLETED");
                            return;
                        }

                        LogActiveReentryForcedFlattenSkipIfNeeded(utcNow);
                        return;
                    }

                    _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_SKIPPED_COMPLETED_TRADE", new Dictionary<string, object?>
                    {
                        ["trading_date"] = TradingDate,
                        ["session_class"] = Session,
                        ["stream"] = Stream,
                        ["instrument"] = ExecutionInstrument,
                        ["original_intent_id"] = _journal.OriginalIntentId ?? "",
                        ["entry_filled_qty"] = originalEntry.EntryFilledQuantityTotal,
                        ["exit_filled_qty"] = originalEntry.ExitFilledQuantityTotal,
                        ["trade_completed"] = originalEntry.TradeCompleted,
                        ["completion_reason"] = originalEntry.CompletionReason ?? ""
                    });
                    _ = Commit(utcNow, "TRADE_COMPLETED_BEFORE_FORCED_FLATTEN", "TRADE_COMPLETED");
                    return;
                }
            }
        }
        else if (_executionJournal != null)
        {
            if (TryFindOpenExecutionJournalEntryForStream(TradingDate, Stream, out var openEntry) && openEntry != null)
            {
                _journal.OriginalIntentId = openEntry.IntentId;
                hasOpenOriginalPosition = true;
            }
            else
            {
                var completedFlatten = FindCompletedFlattenTradeForCurrentStream();
                if (TryArmSessionCloseReentryFromCompletedFlatten(utcNow, completedFlatten, "stream_completed_flatten_without_original_intent"))
                    return;

                if (HasCompletedTradeForCurrentStream())
                {
                    _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_SKIPPED_COMPLETED_TRADE", new Dictionary<string, object?>
                    {
                        ["trading_date"] = TradingDate,
                        ["session_class"] = Session,
                        ["stream"] = Stream,
                        ["instrument"] = ExecutionInstrument,
                        ["original_intent_id"] = "",
                        ["note"] = "Stream has completed trade and no remaining open journal quantity"
                    });
                    _ = Commit(utcNow, "TRADE_COMPLETED_BEFORE_FORCED_FLATTEN", "TRADE_COMPLETED");
                    return;
                }
            }
        }
        
        if (!hasOpenOriginalPosition)
        {
            if (TryDeferForcedFlattenForLivePreEntryBrackets(utcNow))
                return;

            // Pre-entry forced flatten: only terminal once live entry brackets are proven gone.
            if (!Commit(utcNow, "NO_TRADE_FORCED_FLATTEN_PRE_ENTRY", "FORCED_FLATTEN_MARKET_CLOSE"))
                return;
            return;
        }
        
        // Resolve OriginalIntentId if not already stored (needed for flatten + reentry)
        if (string.IsNullOrWhiteSpace(_journal.OriginalIntentId) && _executionJournal != null)
        {
            var pattern = $"{TradingDate}_{Stream}_*.json";
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(_robotStateRoot);
            try
            {
                if (Directory.Exists(journalDir))
                {
                    var files = Directory.GetFiles(journalDir, pattern);
                    foreach (var file in files)
                    {
                        try
                        {
                            var json = File.ReadAllText(file);
                            var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                            if (entry != null &&
                                (entry.EntryFilled || entry.EntryFilledQuantityTotal > 0) &&
                                !entry.TradeCompleted &&
                                ExecutionJournal.GetEntryRemainingOpenQuantity(entry) > 0)
                            {
                                _journal.OriginalIntentId = entry.IntentId;
                                break;
                            }
                        }
                        catch { /* Skip corrupted files */ }
                    }
                }
            }
            catch { /* Fail-safe: continue without OriginalIntentId */ }
        }
        
        if (string.IsNullOrWhiteSpace(_journal.OriginalIntentId))
        {
            LogHealth("CRITICAL", "FORCED_FLATTEN_FAILED", $"Cannot flatten: OriginalIntentId not found for stream {Stream}",
                new { stream = Stream, trading_date = TradingDate });
            LogSessionCloseForcedFlattenFailed(utcNow, "NO_ORIGINAL_INTENT",
                $"Cannot flatten: OriginalIntentId not found for stream {Stream}",
                new Dictionary<string, object?> { ["stream"] = Stream });
            _engine?.OnForcedFlattenFailed(ExecutionInstrument, "FORCED_FLATTEN_NO_ORIGINAL_INTENT", utcNow);
            return;
        }
        
        // Phase 1: Request flatten (session-close queue when supported). Broker-flat confirmation
        // is handled asynchronously by the flatten fill/reconciliation path so the engine tick
        // thread never blocks playback waiting for NinjaTrader callbacks.
        const int SESSION_FORCED_FLATTEN_BROKER_WAIT_SECONDS = 90;
        const int SESSION_FORCED_FLATTEN_RETRY_WAIT_SECONDS = 30;
        if (_executionAdapter == null)
        {
            LogHealth("CRITICAL", "FORCED_FLATTEN_FAILED", "No execution adapter for forced flatten",
                new { original_intent_id = _journal.OriginalIntentId, instrument = ExecutionInstrument });
            LogSessionCloseForcedFlattenFailed(utcNow, "NO_ADAPTER", "No execution adapter for forced flatten",
                new Dictionary<string, object?>
                {
                    ["original_intent_id"] = _journal.OriginalIntentId ?? ""
                });
            _engine?.OnForcedFlattenFailed(ExecutionInstrument, "FORCED_FLATTEN_FAILED", utcNow);
            return;
        }

        FlattenResult? sessionCloseRequest = null;
        if (_executionAdapter is NinjaTraderSimAdapter simSession)
            sessionCloseRequest = simSession.RequestSessionCloseFlattenImmediate(_journal.OriginalIntentId, ExecutionInstrument, utcNow);

        if (sessionCloseRequest == null)
        {
            var legacyFlatten = _executionAdapter.Flatten(_journal.OriginalIntentId, ExecutionInstrument, utcNow);
            if (!IsFlattenRequestAcceptedPendingBroker(legacyFlatten))
            {
                LogHealth("CRITICAL", "FORCED_FLATTEN_FAILED", $"Flatten request rejected: {legacyFlatten?.ErrorMessage ?? "Unknown"}",
                    new { original_intent_id = _journal.OriginalIntentId, instrument = ExecutionInstrument });
                LogSessionCloseForcedFlattenFailed(utcNow, "DELEGATE_REJECTED",
                    $"Flatten request rejected: {legacyFlatten?.ErrorMessage ?? "Unknown"}",
                    new Dictionary<string, object?>
                    {
                        ["original_intent_id"] = _journal.OriginalIntentId ?? "",
                        ["error_message"] = legacyFlatten?.ErrorMessage ?? ""
                    });
                _engine?.OnForcedFlattenFailed(ExecutionInstrument, "FORCED_FLATTEN_FAILED", utcNow);
                return;
            }
        }

        var flattenRequestPath = sessionCloseRequest != null ? "session_close_enqueue" : "flatten_delegate";

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "FORCED_FLATTEN_REQUEST_SUBMITTED", State.ToString(),
            new { original_intent_id = _journal.OriginalIntentId, path = flattenRequestPath }));

        _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_REQUEST_SUBMITTED", new Dictionary<string, object?>
        {
            ["trading_date"] = TradingDate,
            ["session_class"] = Session,
            ["stream"] = Stream,
            ["instrument"] = ExecutionInstrument,
            ["original_intent_id"] = _journal.OriginalIntentId ?? "",
            ["path"] = flattenRequestPath,
            ["session_close_forced_flatten"] = true
        });

        // Once the session-close flatten has been accepted, the original entry is no longer allowed
        // to continue as a normal in-session trade. Persist this before broker confirmation so a
        // slow replay/order callback cannot turn the stream into STREAM_STAND_DOWN and lose reentry.
        _journal.ExecutionInterruptedByClose = true;
        _journal.ForcedFlattenTimestamp = utcNow;
        _journals.Save(_journal);

        if (ShouldDeferSessionCloseBrokerFlatConfirmation())
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "FORCED_FLATTEN_BROKER_CONFIRM_DEFERRED", State.ToString(),
                new
                {
                    original_intent_id = _journal.OriginalIntentId,
                    instrument = ExecutionInstrument,
                    path = flattenRequestPath,
                    note = "Session-close flatten queued; broker-flat confirmation will be handled by the flatten fill/reconciliation path."
                }));

            _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_BROKER_CONFIRM_DEFERRED", new Dictionary<string, object?>
            {
                ["trading_date"] = TradingDate,
                ["session_class"] = Session,
                ["stream"] = Stream,
                ["instrument"] = ExecutionInstrument,
                ["original_intent_id"] = _journal.OriginalIntentId ?? "",
                ["path"] = flattenRequestPath,
                ["session_close_forced_flatten"] = true,
                ["note"] = "Session-close flatten queued; broker-flat confirmation will be handled by the flatten fill/reconciliation path."
            });
            return;
        }

        var wallPollDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(SESSION_FORCED_FLATTEN_BROKER_WAIT_SECONDS);
        var brokerFlat = TryWaitBrokerFlatForExecutionInstrument(ExecutionInstrument, wallPollDeadlineUtc, utcNow, out var remainingQty);
        if (!brokerFlat)
        {
            _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_BROKER_TIMEOUT", new Dictionary<string, object?>
            {
                ["trading_date"] = TradingDate,
                ["session_class"] = Session,
                ["stream"] = Stream,
                ["instrument"] = ExecutionInstrument,
                ["original_intent_id"] = _journal.OriginalIntentId ?? "",
                ["remaining_qty"] = remainingQty,
                ["wait_seconds"] = SESSION_FORCED_FLATTEN_BROKER_WAIT_SECONDS,
                ["session_close_forced_flatten"] = true
            });

            var retryQueued = false;
            try
            {
                retryQueued = _executionAdapter.TryEnqueueEmergencyFlattenProtective(ExecutionInstrument, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_EMERGENCY_RETRY_REJECTED", new Dictionary<string, object?>
                {
                    ["trading_date"] = TradingDate,
                    ["session_class"] = Session,
                    ["stream"] = Stream,
                    ["instrument"] = ExecutionInstrument,
                    ["original_intent_id"] = _journal.OriginalIntentId ?? "",
                    ["remaining_qty"] = remainingQty,
                    ["error"] = ex.Message,
                    ["session_close_forced_flatten"] = true
                });
            }

            _engine?.LogEngineEvent(utcNow, retryQueued
                    ? "FORCED_FLATTEN_EMERGENCY_RETRY_ENQUEUED"
                    : "FORCED_FLATTEN_EMERGENCY_RETRY_UNSUPPORTED",
                new Dictionary<string, object?>
                {
                    ["trading_date"] = TradingDate,
                    ["session_class"] = Session,
                    ["stream"] = Stream,
                    ["instrument"] = ExecutionInstrument,
                    ["original_intent_id"] = _journal.OriginalIntentId ?? "",
                    ["remaining_qty"] = remainingQty,
                    ["retry_wait_seconds"] = retryQueued ? SESSION_FORCED_FLATTEN_RETRY_WAIT_SECONDS : 0,
                    ["session_close_forced_flatten"] = true
                });

            if (retryQueued)
            {
                var retryDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(SESSION_FORCED_FLATTEN_RETRY_WAIT_SECONDS);
                brokerFlat = TryWaitBrokerFlatForExecutionInstrument(ExecutionInstrument, retryDeadlineUtc, utcNow, out remainingQty);
            }

            if (!brokerFlat)
            {
                LogHealth("WARN", "FORCED_FLATTEN_CONFIRM_PENDING",
                    $"Broker flat not confirmed within {SESSION_FORCED_FLATTEN_BROKER_WAIT_SECONDS + (retryQueued ? SESSION_FORCED_FLATTEN_RETRY_WAIT_SECONDS : 0)}s (remaining qty={remainingQty})",
                    new { original_intent_id = _journal.OriginalIntentId, instrument = ExecutionInstrument, remaining_qty = remainingQty, retry_queued = retryQueued });
                _engine?.OnForcedFlattenFailed(ExecutionInstrument, "FORCED_FLATTEN_BROKER_TIMEOUT", utcNow);
                return;
            }
        }

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "FORCED_FLATTEN_POSITION_CLOSED", State.ToString(),
            new { original_intent_id = _journal.OriginalIntentId, note = "Broker flat confirmed before market close" }));

        _engine?.LogEngineEvent(utcNow, "FLATTEN_BROKER_FLAT_CONFIRMED", new Dictionary<string, object?>
        {
            ["trading_date"] = TradingDate,
            ["session_class"] = Session,
            ["stream"] = Stream,
            ["instrument"] = ExecutionInstrument,
            ["original_intent_id"] = _journal.OriginalIntentId ?? "",
            ["note"] = "Session forced flatten — broker flat confirmed before market close",
            ["session_close_forced_flatten"] = true
        });
        
        // Phase 2: Cancel protective orders (stop and target)
        var ordersCancelled = false;
        if (_executionAdapter is NinjaTraderSimAdapter simAdapter)
        {
            try
            {
                ordersCancelled = simAdapter.CancelIntentOrders(_journal.OriginalIntentId, utcNow);
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "FORCED_FLATTEN_ORDERS_CANCEL_ERROR", State.ToString(),
                    new { error = ex.Message, exception_type = ex.GetType().Name }));
            }
        }
        
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "FORCED_FLATTEN_ORDERS_CANCELLED", State.ToString(),
            new { original_intent_id = _journal.OriginalIntentId, cancelled = ordersCancelled, note = "Protective orders cancelled" }));
        
        // Phase 3: Persist state for reentry
        _journal.ExecutionInterruptedByClose = true;
        _journal.ForcedFlattenTimestamp = utcNow;
        _journals.Save(_journal);
        
        // Phase 4: Verify no exposure remains (risk guarantee)
        var exposureVerifiedOk = false;
        try
        {
            var snap = _executionAdapter.GetAccountSnapshot(utcNow);
            var posQty = snap?.Positions?
                .Where(p => string.Equals(p.Instrument, ExecutionInstrument, StringComparison.OrdinalIgnoreCase))
                .Sum(p => p.Quantity) ?? 0;
            if (posQty != 0)
            {
                LogHealth("CRITICAL", "FORCED_FLATTEN_EXPOSURE_REMAINING",
                    $"Position still open after flatten: {ExecutionInstrument} qty={posQty}",
                    new { instrument = ExecutionInstrument, quantity = posQty, original_intent_id = _journal.OriginalIntentId });
                LogSessionCloseExposureRemainingEngine(utcNow, posQty);
                LogHealth("CRITICAL", "MANUAL_FLATTEN_REQUIRED",
                    $"Operator must manually flatten {ExecutionInstrument} — automated flatten did not close position",
                    new { instrument = ExecutionInstrument, quantity = posQty });
                LogSessionCloseManualFlattenRequiredEngine(utcNow, posQty);
                _engine?.OnForcedFlattenFailed(ExecutionInstrument, "FORCED_FLATTEN_EXPOSURE_REMAINING", utcNow);
            }
            else
            {
                exposureVerifiedOk = true;
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "FORCED_FLATTEN_VERIFY_ERROR", State.ToString(),
                new { error = ex.Message, note = "Exposure verification failed" }));
        }
        
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "FORCED_FLATTEN_MARKET_CLOSE", State.ToString(),
            new
            {
                execution_interrupted = true,
                forced_flatten_timestamp = utcNow.ToString("o"),
                original_intent_id = _journal.OriginalIntentId,
                slot_status = _journal.SlotStatus.ToString(),
                note = "Pre-close flatten complete - slot remains ACTIVE for market-open reentry"
            }));

        if (exposureVerifiedOk)
        {
            _engine?.LogEngineEvent(utcNow, "SESSION_FORCED_FLATTENED", new Dictionary<string, object?>
            {
                ["trading_date"] = TradingDate,
                ["session_class"] = Session,
                ["stream"] = Stream,
                ["instrument"] = ExecutionInstrument,
                ["original_intent_id"] = _journal.OriginalIntentId ?? "",
                ["session_close_forced_flatten"] = true,
                ["note"] = "Session forced flatten workflow complete — broker flat, protective handling finished, exposure verified zero"
            });
        }
    }
    
    /// <summary>
    /// Handle slot expiry (next slot time reached).
    /// Only flattens positions that are still open. If ExecutionInterruptedByClose, original position
    /// was already flattened at FlattenTriggerUtc; only reentry position (if filled) may need flatten.
    /// </summary>
    private void HandleSlotExpiry(DateTimeOffset utcNow)
    {
        if (_journal.SlotStatus != SlotStatus.ACTIVE)
        {
            return; // Already expired or terminal
        }
        
        // Exit position at market if open (via execution adapter)
        // Original entry: only flatten if NOT already closed by forced flatten (ExecutionInterruptedByClose)
        // Reentry: always flatten if reentry filled (reentry position may still be open at slot expiry)
        if (_executionAdapter != null)
        {
            // Flatten original entry only if it is still open. A completed original may be
            // followed by a linked reentry lifecycle that owns the live exposure.
            if (ShouldFlattenOriginalIntentAtSlotExpiry())
            {
                try
                {
                    _executionAdapter.Flatten(_journal.OriginalIntentId, ExecutionInstrument, utcNow);
                }
                catch
                {
                    // Log error but continue with expiry
                }
            }
            
            // Flatten re-entry position if re-entry happened (reentry is never flattened at session close)
            if (_journal.ReentryFilled && !string.IsNullOrWhiteSpace(_journal.ReentryIntentId))
            {
                try
                {
                    _executionAdapter.Flatten(_journal.ReentryIntentId, ExecutionInstrument, utcNow);
                }
                catch
                {
                    // Log error but continue with expiry
                }
            }
        }
        
        // Cancel orders for both intents (idempotent - orders may already be cancelled)
        if (_executionAdapter is NinjaTraderSimAdapter simAdapter)
        {
            if (!string.IsNullOrWhiteSpace(_journal.OriginalIntentId))
            {
                try { simAdapter.CancelIntentOrders(_journal.OriginalIntentId, utcNow); }
                catch { /* Log error but continue */ }
            }
            else if (_brkLongRounded.HasValue && _brkShortRounded.HasValue)
            {
                // Pre-entry: cancel long/short entry bracket intents (OriginalIntentId empty when no fill)
                var longIntentId = ComputeIntentId("Long", _brkLongRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_LONG");
                var shortIntentId = ComputeIntentId("Short", _brkShortRounded.Value, SlotTimeUtc, "ENTRY_STOP_BRACKET_SHORT");
                try
                {
                    var routedCancelCount = _executionAdapter.RequestSessionCloseCancelIntents(new[] { longIntentId, shortIntentId }, ExecutionInstrument, utcNow);
                    if (routedCancelCount <= 0)
                    {
                        try { simAdapter.CancelIntentOrders(longIntentId, utcNow); } catch { /* Log error but continue */ }
                        try { simAdapter.CancelIntentOrders(shortIntentId, utcNow); } catch { /* Log error but continue */ }
                    }
                }
                catch { /* Log error but continue */ }
            }
            if (_journal.ReentryFilled && !string.IsNullOrWhiteSpace(_journal.ReentryIntentId))
            {
                try { simAdapter.CancelIntentOrders(_journal.ReentryIntentId, utcNow); }
                catch { /* Log error but continue */ }
            }
        }
        
        // Terminal commit sets SlotStatus (EXPIRED) and SLOT_STATUS_CHANGED after durable write
        if (!Commit(utcNow, "SLOT_EXPIRED", "SLOT_EXPIRED"))
            return;

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "SLOT_EXPIRED", State.ToString(),
            new
            {
                next_slot_time_utc = _journal.NextSlotTimeUtc?.ToString("o") ?? "NULL",
                slot_status = _journal.SlotStatus.ToString(),
                note = "Slot expired at next slot time"
            }));
    }

    private bool ShouldFlattenOriginalIntentAtSlotExpiry()
    {
        if (string.IsNullOrWhiteSpace(_journal.OriginalIntentId) || _journal.ExecutionInterruptedByClose)
            return false;

        if (TryGetLifecycleEntry(_journal.OriginalIntentId, out var original) && original != null)
        {
            if (original.TradeCompleted)
                return false;

            if (original.EntryFilled && ExecutionJournal.GetEntryRemainingOpenQuantity(original) <= 0)
                return false;
        }

        return true;
    }
}
