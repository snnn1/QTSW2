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

    private struct JournalCommitRollback
    {
        public bool Committed;
        public string? CommitReason;
        public bool EntryDetected;
        public string LastState;
        public string LastUpdateUtc;
        public string? TimetableHashAtCommit;
        public StreamTerminalState? TerminalState;
        public SlotStatus SlotStatus;
    }

    private JournalCommitRollback CaptureJournalCommitRollback()
    {
        return new JournalCommitRollback
        {
            Committed = _journal.Committed,
            CommitReason = _journal.CommitReason,
            EntryDetected = _journal.EntryDetected,
            LastState = _journal.LastState,
            LastUpdateUtc = _journal.LastUpdateUtc,
            TimetableHashAtCommit = _journal.TimetableHashAtCommit,
            TerminalState = _journal.TerminalState,
            SlotStatus = _journal.SlotStatus
        };
    }

    private void RestoreJournalCommitRollback(in JournalCommitRollback r)
    {
        _journal.Committed = r.Committed;
        _journal.CommitReason = r.CommitReason;
        _journal.EntryDetected = r.EntryDetected;
        _journal.LastState = r.LastState;
        _journal.LastUpdateUtc = r.LastUpdateUtc;
        _journal.TimetableHashAtCommit = r.TimetableHashAtCommit;
        _journal.TerminalState = r.TerminalState;
        _journal.SlotStatus = r.SlotStatus;
    }

    private bool HasCompletedTradeForCurrentStream()
    {
        if (_executionJournal == null)
            return false;

        // During a forced-flatten/reentry episode, the original intent completing must not make
        // the stream look terminal while the reentry lifecycle is still unresolved.
        if (_journal.ExecutionInterruptedByClose && !_journal.ReentryFilled)
            return false;

        if (!string.IsNullOrWhiteSpace(_journal.ReentryIntentId))
        {
            if (_journal.ReentryFilled)
                return _executionJournal.IsIntentCompleted(_journal.ReentryIntentId, TradingDate, Stream);

            if (_journal.ReentrySubmitted || _journal.ReentrySubmitFailureCount > 0)
                return false;
        }

        if (!string.IsNullOrWhiteSpace(_journal.OriginalIntentId))
            return _executionJournal.IsIntentCompleted(_journal.OriginalIntentId, TradingDate, Stream);

        return _executionJournal.HasCompletedTradeForStream(TradingDate, Stream);
    }

    private bool HasEntryFillForCurrentStream()
    {
        if (_executionJournal == null)
            return _entryDetected;

        if (!string.IsNullOrWhiteSpace(_journal.ReentryIntentId))
        {
            var reentry = _executionJournal.GetEntry(_journal.ReentryIntentId, TradingDate, Stream);
            if (reentry?.EntryFilled == true)
                return true;

            if (_journal.ReentrySubmitted || _journal.ReentrySubmitFailureCount > 0)
                return false;
        }

        if (!string.IsNullOrWhiteSpace(_journal.OriginalIntentId))
        {
            var original = _executionJournal.GetEntry(_journal.OriginalIntentId, TradingDate, Stream);
            if (original?.EntryFilled == true)
                return true;
        }

        return _executionJournal.HasEntryFillForStream(TradingDate, Stream) || _entryDetected;
    }

    private static bool IsCompletedFlattenTrade(ExecutionJournalEntry? entry)
    {
        if (entry == null || !entry.EntryFilled || entry.EntryFilledQuantityTotal <= 0 || !entry.TradeCompleted)
            return false;

        return string.Equals(entry.ExitOrderType, "FLATTEN", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(entry.CompletionReason, "FLATTEN", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset GetJournalEntryCompletionSortKey(ExecutionJournalEntry entry)
    {
        foreach (var raw in new[] { entry.CompletedAtUtc, entry.ExitFilledObservedAtUtc, entry.ExitFilledAtUtc })
        {
            if (DateTimeOffset.TryParse(raw, out var parsed))
                return parsed;
        }

        return DateTimeOffset.MinValue;
    }

    private ExecutionJournalEntry? FindCompletedFlattenTradeForCurrentStream()
    {
        return LoadExecutionJournalEntriesForStream(TradingDate, Stream)
            .Where(IsCompletedFlattenTrade)
            .OrderByDescending(GetJournalEntryCompletionSortKey)
            .FirstOrDefault();
    }

    private bool TryArmSessionCloseReentryFromCompletedFlatten(DateTimeOffset utcNow, ExecutionJournalEntry? completedEntry, string triggerSource)
    {
        if (!IsCompletedFlattenTrade(completedEntry))
            return false;

        if (HasAnyReentryLifecycle())
            return false;

        var originalIntentId = completedEntry!.IntentId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(originalIntentId))
            return false;

        _journal.Committed = false;
        _journal.SlotStatus = SlotStatus.ACTIVE;
        _journal.ExecutionInterruptedByClose = true;
        _journal.ForcedFlattenTimestamp ??= utcNow;
        _journal.OriginalIntentId = originalIntentId;
        _journal.LastUpdateUtc = utcNow.ToString("o");
        var persisted = _journals.Save(_journal);

        var payload = new
        {
            original_intent_id = originalIntentId,
            execution_instrument = ExecutionInstrument,
            completion_reason = completedEntry.CompletionReason ?? "",
            exit_order_type = completedEntry.ExitOrderType ?? "",
            exit_filled_qty = completedEntry.ExitFilledQuantityTotal,
            trigger_source = triggerSource,
            persisted,
            note = "Session-close flatten completion must leave the stream ACTIVE/interrupted for market-open reentry instead of committing as an ordinary completed trade."
        };

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "FORCED_FLATTEN_COMPLETED_LATE_REENTRY_ARMED", State.ToString(), payload));

        _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_COMPLETED_LATE_REENTRY_ARMED", new Dictionary<string, object?>
        {
            ["trading_date"] = TradingDate,
            ["session_class"] = Session,
            ["stream"] = Stream,
            ["instrument"] = ExecutionInstrument,
            ["original_intent_id"] = originalIntentId,
            ["completion_reason"] = completedEntry.CompletionReason ?? "",
            ["exit_order_type"] = completedEntry.ExitOrderType ?? "",
            ["exit_filled_qty"] = completedEntry.ExitFilledQuantityTotal,
            ["trigger_source"] = triggerSource,
            ["persisted"] = persisted,
            ["session_close_forced_flatten"] = true,
            ["note"] = "Completed FLATTEN at session-close was classified as interrupted lifecycle so reentry can evaluate at next market open."
        });

        return true;
    }

    private bool TryCommitCompletedTradeAtMarketClose(DateTimeOffset utcNow)
    {
        if (!HasCompletedTradeForCurrentStream())
            return false;

        var reason = _journal.ReentryFilled
            ? "REENTRY_TRADE_COMPLETED_MARKET_CLOSE"
            : "TRADE_COMPLETED_MARKET_CLOSE";
        return Commit(utcNow, reason, "TRADE_COMPLETED");
    }

    private bool HasAnyReentryLifecycle()
        => !string.IsNullOrWhiteSpace(_journal.ReentryIntentId) ||
           _journal.ReentrySubmitPending ||
           _journal.ReentrySubmitted ||
           _journal.ReentryFilled ||
           _journal.ProtectionSubmitted ||
           _journal.ProtectionAccepted;

    private bool IsReentryLifecycleCompleted()
    {
        if (_executionJournal == null || string.IsNullOrWhiteSpace(_journal.ReentryIntentId))
            return false;

        return _executionJournal.IsIntentCompleted(_journal.ReentryIntentId, TradingDate, Stream);
    }

    private void LogActiveReentryForcedFlattenSkipIfNeeded(DateTimeOffset utcNow)
    {
        var key = string.Join("|",
            TradingDate,
            Stream,
            _journal.OriginalIntentId ?? "",
            _journal.ReentryIntentId ?? "",
            _journal.ReentrySubmitted,
            _journal.ReentryFilled,
            _journal.ProtectionSubmitted,
            _journal.ProtectionAccepted);

        var shouldLog = !string.Equals(_lastActiveReentryForcedFlattenSkipLogKey, key, StringComparison.Ordinal) ||
                        !_lastActiveReentryForcedFlattenSkipLogUtc.HasValue ||
                        (utcNow - _lastActiveReentryForcedFlattenSkipLogUtc.Value).TotalMinutes >=
                        ActiveReentryForcedFlattenSkipLogIntervalMinutes;
        if (!shouldLog)
            return;

        _lastActiveReentryForcedFlattenSkipLogKey = key;
        _lastActiveReentryForcedFlattenSkipLogUtc = utcNow;
        _engine?.LogEngineEvent(utcNow, "FORCED_FLATTEN_SKIPPED_ACTIVE_REENTRY", new Dictionary<string, object?>
        {
            ["trading_date"] = TradingDate,
            ["session_class"] = Session,
            ["stream"] = Stream,
            ["instrument"] = ExecutionInstrument,
            ["original_intent_id"] = _journal.OriginalIntentId ?? "",
            ["reentry_intent_id"] = _journal.ReentryIntentId ?? "",
            ["reentry_submitted"] = _journal.ReentrySubmitted,
            ["reentry_filled"] = _journal.ReentryFilled,
            ["protection_submitted"] = _journal.ProtectionSubmitted,
            ["protection_accepted"] = _journal.ProtectionAccepted,
            ["detail_log_interval_minutes"] = ActiveReentryForcedFlattenSkipLogIntervalMinutes,
            ["note"] = "Original intent is complete, but the stream has an unresolved reentry lifecycle; leaving stream ACTIVE for next-slot expiry."
        });
    }

    /// <summary>
    /// Terminal commit: persists <see cref="StreamJournal"/> with Committed=true, then sets <see cref="State"/> to DONE.
    /// Option A: if <see cref="JournalStore.Save"/> fails, in-memory journal is rolled back and state is not DONE.
    /// </summary>
    /// <returns>True if the stream is durably committed (or was already committed); false if persistence failed.</returns>
    private bool Commit(DateTimeOffset utcNow, string commitReason, string eventType)
    {
        if (_journal.Committed)
        {
            State = StreamState.DONE;
            return true;
        }

        var rollback = CaptureJournalCommitRollback();
        var hasCompletedTradeForStream = HasCompletedTradeForCurrentStream();
        if (hasCompletedTradeForStream &&
            (commitReason == "NO_TRADE_MARKET_CLOSE" || commitReason.Contains("NO_TRADE")))
        {
            commitReason = _journal.ReentryFilled
                ? "REENTRY_TRADE_COMPLETED_MARKET_CLOSE"
                : "TRADE_COMPLETED_MARKET_CLOSE";
            eventType = "TRADE_COMPLETED";
        }

        _journal.Committed = true;
        _journal.CommitReason = commitReason;
        // CRITICAL FIX: Make execution journal authoritative for EntryDetected at commit
        // After removing RecordIntendedEntry(), _entryDetected may be false even after fill
        // Execution journal is ground truth for fills (already used by restart recovery)
        _journal.EntryDetected =
            (_executionJournal?.HasEntryFillForStream(TradingDate, Stream) == true)
            || _entryDetected;
        _journal.LastState = StreamState.DONE.ToString();
        _journal.LastUpdateUtc = utcNow.ToString("o");
        _journal.TimetableHashAtCommit = _timetableHash;

        // Determine terminal state based on commit reason and trade completion
        _journal.TerminalState = DetermineTerminalState(commitReason, utcNow);

        // SLOT PERSISTENCE: Set SlotStatus based on commit reason
        var previousSlotStatus = rollback.SlotStatus;
        SlotStatus newSlotStatus;
        if (hasCompletedTradeForStream)
        {
            newSlotStatus = SlotStatus.COMPLETE;
        }
        else if (commitReason == "SLOT_EXPIRED")
        {
            newSlotStatus = SlotStatus.EXPIRED;
        }
        else if (commitReason == "NO_TRADE_MARKET_CLOSE" || commitReason.Contains("NO_TRADE"))
        {
            newSlotStatus = SlotStatus.NO_TRADE;
        }
        else if (commitReason.Contains("FAILED") || commitReason.Contains("ERROR") || commitReason.Contains("CORRUPTION") || commitReason == "STREAM_STAND_DOWN")
        {
            newSlotStatus = SlotStatus.FAILED_RUNTIME;
        }
        else
        {
            newSlotStatus = SlotStatus.COMPLETE;
        }

        _journal.SlotStatus = newSlotStatus;
        var slotStatusChanged = previousSlotStatus != newSlotStatus;

        if (!_journals.Save(_journal))
        {
            RestoreJournalCommitRollback(in rollback);
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STREAM_COMMIT_PERSIST_FAILED", State.ToString(),
                new
                {
                    commit_reason = commitReason,
                    event_type = eventType,
                    journal_path_hint = $"{TradingDate}_{Stream}.json",
                    note = "Journal disk write failed — stream not marked DONE; will retry on next transition"
                }));
            _engine?.TryAppendKeyEventStreamCommitPersistFailed(utcNow, Stream, Instrument ?? "", commitReason, eventType,
                State.ToString());
            return false;
        }

        if (slotStatusChanged)
        {
            LogHealth("INFO", "SLOT_STATUS_CHANGED", $"Slot status changed: {previousSlotStatus} -> {newSlotStatus}",
                new
                {
                    previous_status = previousSlotStatus.ToString(),
                    new_status = newSlotStatus.ToString(),
                    commit_reason = commitReason,
                    slot_instance_key = _journal.SlotInstanceKey
                });
        }

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "JOURNAL_WRITTEN", StreamState.DONE.ToString(),
            new
            {
                committed = true,
                commit_reason = commitReason,
                terminal_state = _journal.TerminalState?.ToString() ?? "NULL",
                slot_status = _journal.SlotStatus.ToString(),
                timetable_hash_at_commit = _timetableHash,
                execution_instrument = ExecutionInstrument,  // PHASE 3: Execution identity
                canonical_instrument = CanonicalInstrument   // PHASE 3: Canonical identity
            }));

        State = StreamState.DONE;
        // PHASE 3: Include both identities in commit event (RANGE_INVALIDATED, STREAM_STAND_DOWN, etc.)
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            eventType, State.ToString(),
            new
            {
                committed = true,
                commit_reason = commitReason,
                terminal_state = _journal.TerminalState?.ToString() ?? "NULL",
                slot_status = _journal.SlotStatus.ToString(),
                execution_instrument = ExecutionInstrument,  // PHASE 3: Execution identity
                canonical_instrument = CanonicalInstrument   // PHASE 3: Canonical identity
            }));
        return true;
    }

    /// <summary>
    /// Determine terminal state based on commit reason and trade completion status.
    /// </summary>
    private StreamTerminalState DetermineTerminalState(string commitReason, DateTimeOffset utcNow)
    {
        // Check if trade was completed (from execution journal)
        var tradeCompleted = HasCompletedTradeForCurrentStream();

        // Classify based on commit reason and trade completion
        if (tradeCompleted)
        {
            return StreamTerminalState.TRADE_COMPLETED;
        }

        // Check for zero-bar hydration (distinct from generic NO_TRADE)
        // Zero-bar hydration occurs when CSV missing, BarsRequest failed, or hard timeout with 0 bars
        if (_hadZeroBarHydration && 
            (commitReason.Contains("NO_TRADE") || 
             commitReason == "NO_TRADE_MARKET_CLOSE" ||
             commitReason.Contains("PRE_HYDRATION") ||
             commitReason.Contains("TIMEOUT")))
        {
            return StreamTerminalState.ZERO_BAR_HYDRATION;
        }

        // Classify based on commit reason
        if (commitReason == "STREAM_STAND_DOWN" || 
            commitReason.Contains("FAILED") || 
            commitReason.Contains("ERROR") ||
            commitReason.Contains("CORRUPTION"))
        {
            return StreamTerminalState.FAILED_RUNTIME;
        }

        if (commitReason == "NO_TRADE_MARKET_CLOSE" || 
            commitReason == "NO_TRADE_LATE_START_MISSED_BREAKOUT" ||
            commitReason.Contains("NO_TRADE"))
        {
            return StreamTerminalState.NO_TRADE;
        }

        if (State == StreamState.SUSPENDED_DATA_INSUFFICIENT)
        {
            return StreamTerminalState.SUSPENDED_DATA;
        }

        // Default: NO_TRADE if no other classification applies
        return StreamTerminalState.NO_TRADE;
    }
    
    /// <summary>
    /// PHASE 4: Persist missing data incident record.
    /// </summary>
    private void PersistMissingDataIncident(DateTimeOffset utcNow, string incidentMessage)
    {
        try
        {
            // Get project root from journal path (journals are in projectRoot/state/execution_journals)
            var journalPath = _journals.GetJournalPath(TradingDate, Stream);
            var projectRoot = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(journalPath)));
            
            if (string.IsNullOrEmpty(projectRoot))
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "INCIDENT_PERSIST_ERROR", State.ToString(),
                    new { error = "Could not determine project root from journal path" }));
                return;
            }
            
            var incidentDir = System.IO.Path.Combine(projectRoot, "data", "execution_incidents");
            System.IO.Directory.CreateDirectory(incidentDir);
            
            var incidentPath = System.IO.Path.Combine(incidentDir, $"missing_data_{Stream}_{utcNow:yyyyMMddHHmmss}.json");
            
            var incident = new
            {
                incident_type = "NO_DATA_NO_TRADE",
                timestamp_utc = utcNow.ToString("o"),
                trading_date = TradingDate,
                stream = Stream,
                instrument = Instrument,
                session = Session,
                slot_time_chicago = SlotTimeChicago,
                slot_time_utc = SlotTimeUtc.ToString("o"),
                range_start_chicago = RangeStartChicagoTime.ToString("o"),
                range_start_utc = RangeStartUtc.ToString("o"),
                incident_message = incidentMessage,
                action_taken = "STREAM_COMMITTED_NO_TRADE"
            };
            
            var json = JsonUtil.Serialize(incident);
            System.IO.File.WriteAllText(incidentPath, json);
        }
        catch (Exception ex)
        {
            // Fail loudly: log incident persist failure as ERROR (but do not throw)
            LogCriticalError("INCIDENT_PERSIST_ERROR", ex, utcNow, new
            {
                incident_type = "NO_DATA_NO_TRADE",
                note = "Failed to persist missing data incident file"
            });
        }
    }


    // SIMPLIFICATION: CheckBreakoutEntry() and CheckImmediateEntryAtLock() methods removed
    // Stop brackets (submitted at lock) handle ALL entry scenarios automatically:
    // - If price already beyond breakout → stop fills immediately (marketable stop behavior)
    // - If price not at breakout → stop waits for breakout
    // This eliminates race conditions and reduces complexity from 2 paths to 1:
    // 1. Stop brackets at lock (handle all scenarios - immediate fills and future breakouts)
    //
    // Benefits:
    // - Eliminates race conditions between stop brackets and breakout detection
    // - Simpler state management (no need to check if stop brackets exist)
    // - Single mechanism for breakout entries (stop brackets)
    // - Reduced complexity: 3 entry paths → 2 entry paths
    //
    // OLD CODE (removed):
    // private void CheckBreakoutEntry(DateTimeOffset barUtc, decimal high, decimal low, DateTimeOffset utcNow)
    // {
    //     // This method was removed - stop brackets handle breakouts automatically
    // }

    // REMOVED: RecordIntendedEntry() method - became dead code after removing CheckImmediateEntryAtLock()
    // Stop brackets create Intent objects directly and don't use this method
    // This method handled single-entry logic (pre-submission), but stop brackets work differently (submit both orders at once)

    private void LogNoTradeMarketClose(DateTimeOffset utcNow)
    {
        if (HasCompletedTradeForCurrentStream())
            return;

        _entryDetected = true; // Mark as processed
        _intendedDirection = null;

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "DRYRUN_INTENDED_ENTRY", State.ToString(),
            new
            {
                intended_trade = false,
                direction = (string?)null,
                entry_time_utc = (string?)null,
                entry_time_chicago = (string?)null,
                entry_price = (decimal?)null,
                trigger_reason = "NO_TRADE_MARKET_CLOSE"
            }));
    }

}
