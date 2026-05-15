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
                return TryGetLifecycleEntry(_journal.ReentryIntentId, out var reentryCompleted) &&
                       reentryCompleted?.TradeCompleted == true;

            if (_journal.ReentrySubmitted || _journal.ReentrySubmitFailureCount > 0)
                return false;
        }

        if (!string.IsNullOrWhiteSpace(_journal.OriginalIntentId))
            return TryGetLifecycleEntry(_journal.OriginalIntentId, out var originalCompleted) &&
                   originalCompleted?.TradeCompleted == true;

        return _executionJournal.HasCompletedTradeForStream(TradingDate, Stream);
    }

    private bool HasEntryFillForCurrentStream()
    {
        if (_executionJournal == null)
            return _entryDetected;

        if (!string.IsNullOrWhiteSpace(_journal.ReentryIntentId))
        {
            TryGetLifecycleEntry(_journal.ReentryIntentId, out var reentry);
            if (reentry?.EntryFilled == true)
                return true;

            if (_journal.ReentrySubmitted || _journal.ReentrySubmitFailureCount > 0)
                return false;
        }

        if (!string.IsNullOrWhiteSpace(_journal.OriginalIntentId))
        {
            TryGetLifecycleEntry(_journal.OriginalIntentId, out var original);
            if (original?.EntryFilled == true)
                return true;
        }

        return _executionJournal.HasEntryFillForStream(TradingDate, Stream) || _entryDetected;
    }

    private bool TryGetLifecycleEntry(string? intentId, out ExecutionJournalEntry? entry)
    {
        entry = null;
        if (_executionJournal == null || string.IsNullOrWhiteSpace(intentId))
            return false;

        foreach (var (td, stream) in EnumerateLifecycleJournalKeys())
        {
            entry = _executionJournal.GetEntry(intentId, td, stream);
            if (entry != null)
                return true;
        }

        return false;
    }

    private IEnumerable<(string TradingDate, string Stream)> EnumerateLifecycleJournalKeys()
    {
        yield return (TradingDate, Stream);

        if (string.IsNullOrWhiteSpace(_journal.PriorJournalKey))
            yield break;

        var parts = _journal.PriorJournalKey.Split('_');
        if (parts.Length < 2)
            yield break;

        var priorDate = parts[0];
        var priorStream = string.Join("_", parts.Skip(1));
        if (string.IsNullOrWhiteSpace(priorDate) || string.IsNullOrWhiteSpace(priorStream))
            yield break;

        if (!string.Equals(priorDate, TradingDate, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(priorStream, Stream, StringComparison.OrdinalIgnoreCase))
        {
            yield return (priorDate, priorStream);
        }
    }

    private bool HasOpenLifecycleExposureOrPendingReentry()
    {
        if (!string.IsNullOrWhiteSpace(_journal.ReentryIntentId) &&
            TryGetLifecycleEntry(_journal.ReentryIntentId, out var reentry) &&
            reentry != null &&
            !reentry.TradeCompleted &&
            (reentry.EntrySubmitted || reentry.EntryFilled || reentry.EntryFilledQuantityTotal > 0))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_journal.OriginalIntentId) &&
            TryGetLifecycleEntry(_journal.OriginalIntentId, out var original) &&
            original != null &&
            !original.TradeCompleted &&
            (original.EntrySubmitted || original.EntryFilled || original.EntryFilledQuantityTotal > 0))
        {
            return true;
        }

        if (HasEntryFillForCurrentStream() && !HasCompletedTradeForCurrentStream())
            return true;

        return _journal.ExecutionInterruptedByClose ||
               (HasAnyReentryLifecycle() && !IsReentryLifecycleCompleted());
    }

    private static bool IsCompletedFlattenTrade(ExecutionJournalEntry? entry)
    {
        if (entry == null || !entry.EntryFilled || entry.EntryFilledQuantityTotal <= 0 || !entry.TradeCompleted)
            return false;

        return string.Equals(entry.ExitOrderType, "FLATTEN", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(entry.CompletionReason, "FLATTEN", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(entry.ExitOrderType, CompletionReasons.RECONCILIATION_BROKER_FLAT, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(entry.CompletionReason, CompletionReasons.RECONCILIATION_BROKER_FLAT, StringComparison.OrdinalIgnoreCase);
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

    internal bool MarkSessionCloseInterruptedByGlobalSweep(DateTimeOffset utcNow, string? originalIntentId, string triggerSource)
    {
        if (_journal.Committed || _journal.SlotStatus != SlotStatus.ACTIVE)
            return false;

        var normalizedOriginalIntentId = originalIntentId?.Trim() ?? "";
        var previousInterrupted = _journal.ExecutionInterruptedByClose;
        var previousOriginalIntentId = _journal.OriginalIntentId ?? "";

        _journal.ExecutionInterruptedByClose = true;
        _journal.ForcedFlattenTimestamp ??= utcNow;
        if (string.IsNullOrWhiteSpace(_journal.OriginalIntentId) && !string.IsNullOrWhiteSpace(normalizedOriginalIntentId))
            _journal.OriginalIntentId = normalizedOriginalIntentId;
        _journal.LastUpdateUtc = utcNow.ToString("o");

        var persisted = _journals.Save(_journal);
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "SESSION_CLOSE_GLOBAL_SWEEP_STREAM_INTERRUPTED", State.ToString(),
            new
            {
                original_intent_id = _journal.OriginalIntentId ?? "",
                supplied_original_intent_id = normalizedOriginalIntentId,
                previous_original_intent_id = previousOriginalIntentId,
                execution_instrument = ExecutionInstrument,
                forced_flatten_timestamp_utc = _journal.ForcedFlattenTimestamp?.ToString("o") ?? "",
                previous_interrupted = previousInterrupted,
                trigger_source = triggerSource,
                persisted,
                note = "Broker-level session-close sweep marked the live stream interrupted so later broker-flat reconciliation cannot terminally commit before reentry."
            }));

        return persisted;
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

        return TryGetLifecycleEntry(_journal.ReentryIntentId, out var reentry) &&
               reentry?.TradeCompleted == true;
    }

    private static bool IsIntentionalOpenLifecycleTerminalCommit(string commitReason, string eventType)
    {
        return string.Equals(commitReason, "SLOT_EXPIRED", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(eventType, "SLOT_EXPIRED", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(commitReason, "SINGLE_DAY_PLAYBACK_FORCED_FLATTEN_TERMINAL", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<(string TradingDate, string Stream)> EnumerateLifecycleJournalKeys(
        StreamJournal journal,
        string tradingDate,
        string stream)
    {
        yield return (tradingDate, stream);

        if (string.IsNullOrWhiteSpace(journal.PriorJournalKey))
            yield break;
        if (!TryParseStreamJournalKey(journal.PriorJournalKey, out var priorDate, out var priorStream))
            yield break;
        if (string.Equals(priorDate, tradingDate, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(priorStream, stream, StringComparison.OrdinalIgnoreCase))
            yield break;

        yield return (priorDate, priorStream);
    }

    private bool TryGetLifecycleEntryForJournal(
        StreamJournal journal,
        string tradingDate,
        string stream,
        string? intentId,
        out ExecutionJournalEntry? entry)
    {
        entry = null;
        if (_executionJournal == null || string.IsNullOrWhiteSpace(intentId))
            return false;

        foreach (var (td, journalStream) in EnumerateLifecycleJournalKeys(journal, tradingDate, stream))
        {
            entry = _executionJournal.GetEntry(intentId, td, journalStream);
            if (entry != null)
                return true;
        }

        return false;
    }

    private bool HasEntryFillForJournal(StreamJournal journal, string tradingDate, string stream)
    {
        if (_executionJournal == null)
            return false;

        foreach (var (td, journalStream) in EnumerateLifecycleJournalKeys(journal, tradingDate, stream))
        {
            if (_executionJournal.HasEntryFillForStream(td, journalStream))
                return true;
        }

        return false;
    }

    private bool IsJournalReferencedByCurrentPriorKey(StreamJournal journal)
    {
        return !string.IsNullOrWhiteSpace(_journal.PriorJournalKey) &&
               TryParseStreamJournalKey(_journal.PriorJournalKey, out var priorTradingDate, out var priorStream) &&
               string.Equals(priorTradingDate, journal.TradingDate, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(priorStream, journal.Stream, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCarryForwardTerminalProvenByCurrentJournal(StreamJournal journal)
    {
        if (ReferenceEquals(journal, _journal) || !_journal.Committed)
            return false;
        if (!IsJournalReferencedByCurrentPriorKey(journal) && !IsSameCarryForwardLifecycle(journal))
            return false;

        return _journal.SlotStatus == SlotStatus.COMPLETE ||
               _journal.SlotStatus == SlotStatus.EXPIRED ||
               _journal.SlotStatus == SlotStatus.NO_TRADE ||
               _journal.TerminalState == StreamTerminalState.TRADE_COMPLETED ||
               _journal.TerminalState == StreamTerminalState.NO_TRADE ||
               !string.IsNullOrWhiteSpace(_journal.CommitReason);
    }

    private bool HasCompletedTradeForJournal(StreamJournal journal, string tradingDate, string stream)
    {
        if (IsCarryForwardTerminalProvenByCurrentJournal(journal))
            return true;

        if (_executionJournal == null)
            return false;

        if (journal.ExecutionInterruptedByClose && !journal.ReentryFilled)
            return false;

        if (!string.IsNullOrWhiteSpace(journal.ReentryIntentId))
        {
            TryGetLifecycleEntryForJournal(journal, tradingDate, stream, journal.ReentryIntentId, out var reentry);
            if (journal.ReentryFilled)
                return reentry?.TradeCompleted == true;

            if (journal.ReentrySubmitted || journal.ReentrySubmitFailureCount > 0)
                return false;
        }

        if (!string.IsNullOrWhiteSpace(journal.OriginalIntentId))
            return TryGetLifecycleEntryForJournal(journal, tradingDate, stream, journal.OriginalIntentId, out var original) &&
                   original?.TradeCompleted == true;

        foreach (var (td, journalStream) in EnumerateLifecycleJournalKeys(journal, tradingDate, stream))
        {
            if (_executionJournal.HasCompletedTradeForStream(td, journalStream))
                return true;
        }

        return false;
    }

    private bool HasOpenLifecycleExposureOrPendingReentryForJournal(StreamJournal journal, string tradingDate, string stream)
    {
        if (IsCarryForwardTerminalProvenByCurrentJournal(journal))
            return false;

        if (_executionJournal != null)
        {
            if (!string.IsNullOrWhiteSpace(journal.ReentryIntentId))
            {
                TryGetLifecycleEntryForJournal(journal, tradingDate, stream, journal.ReentryIntentId, out var reentry);
                if (reentry != null &&
                    !reentry.TradeCompleted &&
                    (reentry.EntrySubmitted || reentry.EntryFilled || reentry.EntryFilledQuantityTotal > 0))
                {
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(journal.OriginalIntentId))
            {
                TryGetLifecycleEntryForJournal(journal, tradingDate, stream, journal.OriginalIntentId, out var original);
                if (original != null &&
                    !original.TradeCompleted &&
                    (original.EntrySubmitted || original.EntryFilled || original.EntryFilledQuantityTotal > 0))
                {
                    return true;
                }
            }

            if (HasEntryFillForJournal(journal, tradingDate, stream) &&
                !HasCompletedTradeForJournal(journal, tradingDate, stream))
            {
                return true;
            }
        }

        var hasAnyReentryLifecycle =
            !string.IsNullOrWhiteSpace(journal.ReentryIntentId) ||
            journal.ReentrySubmitPending ||
            journal.ReentrySubmitted ||
            journal.ReentryFilled ||
            journal.ProtectionSubmitted ||
            journal.ProtectionAccepted;

        var reentryCompleted = _executionJournal != null &&
                               !string.IsNullOrWhiteSpace(journal.ReentryIntentId) &&
                               TryGetLifecycleEntryForJournal(journal, tradingDate, stream, journal.ReentryIntentId, out var reentryEntry) &&
                               reentryEntry?.TradeCompleted == true;

        return journal.ExecutionInterruptedByClose ||
               (hasAnyReentryLifecycle && !reentryCompleted);
    }

    private ExecutionAuthorityActionDecision EvaluateTerminalCommitAuthorityForJournal(
        DateTimeOffset utcNow,
        string commitReason,
        string eventType,
        StreamJournal journal,
        string tradingDate,
        string stream,
        string source,
        bool? forceCompletedTradeForStream = null,
        bool? forceOpenLifecycleExposureOrPendingReentry = null)
    {
        var hasCompletedTradeForStream = forceCompletedTradeForStream ?? HasCompletedTradeForJournal(journal, tradingDate, stream);
        var openLifecycleExposureOrPendingReentry = forceOpenLifecycleExposureOrPendingReentry ?? HasOpenLifecycleExposureOrPendingReentryForJournal(journal, tradingDate, stream);
        var intentionalOpenLifecycleTerminalCommit = IsIntentionalOpenLifecycleTerminalCommit(commitReason, eventType);
        var authorityIntentId = journal.ReentryIntentId ?? journal.OriginalIntentId ?? "";
        var activeReentryState = journal.ReentryFilled
            ? "REENTRY_FILLED"
            : journal.ReentrySubmitted
                ? "REENTRY_SUBMITTED"
                : journal.ReentryIntentId != null
                    ? "REENTRY_PENDING"
                    : "";
        var terminalCommitAuthorityFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = source,
            Instrument = ExecutionInstrument,
            CanonicalInstrument = CanonicalInstrument,
            ExecutionInstrumentKey = ExecutionInstrument,
            TradingDate = tradingDate,
            StreamId = stream,
            IntentId = authorityIntentId,
            SubmitPath = "STREAM_COMMIT",
            ExecutionMode = _executionMode.ToString(),
            DecisionUtc = utcNow,
            FrameCreatedUtc = utcNow,
            StreamLifecycleState = ReferenceEquals(journal, _journal) ? State.ToString() : (journal.LastState ?? ""),
            StreamCommitted = journal.Committed,
            ActiveIntentsCount = openLifecycleExposureOrPendingReentry ? 1 : 0,
            ActiveIntentIds = string.IsNullOrWhiteSpace(authorityIntentId)
                ? Array.Empty<string>()
                : new[] { authorityIntentId },
            ActiveReentryState = activeReentryState,
            AuthorityState = "STREAM_COMMIT",
            IsPlayback = _executionMode == ExecutionMode.SIM && _ignoreExistingStreamJournals
        });
        var terminalCommitAuthority = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.TerminalCommit,
            Source = source,
            Instrument = ExecutionInstrument,
            IntentId = authorityIntentId,
            Stream = stream,
            UtcNow = utcNow,
            CommitReason = commitReason,
            EventType = eventType,
            HasOpenLifecycleExposureOrPendingReentry = openLifecycleExposureOrPendingReentry,
            HasCompletedTradeForCurrentStream = hasCompletedTradeForStream,
            IsIntentionalOpenLifecycleTerminalCommit = intentionalOpenLifecycleTerminalCommit,
            AuthorityFrame = terminalCommitAuthorityFrame
        });
        _log.Write(RobotEvents.Base(_time, utcNow, tradingDate, stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "AUTHORITY_FRAME_SNAPSHOT", ReferenceEquals(journal, _journal) ? State.ToString() : (journal.LastState ?? ""),
            ExecutionAuthorityFrameBuilder.ToLogPayload(
                terminalCommitAuthorityFrame,
                action: "STREAM_COMMIT",
                decision: terminalCommitAuthority.Allowed ? "ALLOW" : "DENY",
                denyReason: terminalCommitAuthority.DenyReason)));

        return terminalCommitAuthority;
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
            var rehydrateAuthority = EvaluateTerminalCommitAuthorityForJournal(
                utcNow,
                _journal.CommitReason ?? commitReason,
                eventType,
                _journal,
                TradingDate,
                Stream,
                "StreamStateMachine.Commit.AlreadyCommittedRehydrate");
            if (!rehydrateAuthority.Allowed)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "STREAM_COMMITTED_REHYDRATION_DENIED_BY_AUTHORITY", State.ToString(),
                    new
                    {
                        commit_reason = _journal.CommitReason ?? commitReason,
                        event_type = eventType,
                        original_intent_id = _journal.OriginalIntentId ?? "",
                        reentry_intent_id = _journal.ReentryIntentId ?? "",
                        execution_interrupted_by_close = _journal.ExecutionInterruptedByClose,
                        authority_gate = rehydrateAuthority.GateName,
                        authority_deny_reason = rehydrateAuthority.DenyReason ?? "",
                        note = "Already-committed stream journal was not mirrored to in-memory DONE because fresh authority evidence still sees open lifecycle exposure."
                    }));
                return false;
            }

            TryEnsureCarryForwardTerminalMirror(
                utcNow,
                _journal.CommitReason ?? commitReason,
                eventType,
                _journal.SlotStatus);
            State = StreamState.DONE;
            return true;
        }

        var rollback = CaptureJournalCommitRollback();
        var hasCompletedTradeForStream = HasCompletedTradeForCurrentStream();
        var terminalCommitAuthority = EvaluateTerminalCommitAuthorityForJournal(
            utcNow,
            commitReason,
            eventType,
            _journal,
            TradingDate,
            Stream,
            "StreamStateMachine.Commit");
        if (!terminalCommitAuthority.Allowed)
        {
            _journal.Committed = false;
            _journal.CommitReason = null;
            _journal.SlotStatus = SlotStatus.ACTIVE;
            _journal.LastUpdateUtc = utcNow.ToString("o");
            _journals.Save(_journal);

            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "STREAM_COMMIT_DEFERRED_OPEN_LIFECYCLE", State.ToString(),
                new
                {
                    commit_reason = commitReason,
                    event_type = eventType,
                    original_intent_id = _journal.OriginalIntentId ?? "",
                    reentry_intent_id = _journal.ReentryIntentId ?? "",
                    execution_interrupted_by_close = _journal.ExecutionInterruptedByClose,
                    reentry_submitted = _journal.ReentrySubmitted,
                    reentry_filled = _journal.ReentryFilled,
                    protection_submitted = _journal.ProtectionSubmitted,
                    protection_accepted = _journal.ProtectionAccepted,
                    prior_journal_key = _journal.PriorJournalKey ?? "",
                    authority_gate = terminalCommitAuthority.GateName,
                    authority_deny_reason = terminalCommitAuthority.DenyReason ?? "",
                    note = "Terminal stream commit is deferred while journal/reentry lifecycle evidence remains open."
                }));
            return false;
        }

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

        TryEnsureCarryForwardTerminalMirror(utcNow, commitReason, eventType, newSlotStatus);

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

    private void TryEnsureCarryForwardTerminalMirror(
        DateTimeOffset utcNow,
        string commitReason,
        string eventType,
        SlotStatus slotStatus)
    {
        if (string.IsNullOrWhiteSpace(_journal.PriorJournalKey))
            return;

        var mirrorKey = string.Join("|",
            _journal.TradingDate,
            Stream,
            _journal.PriorJournalKey,
            commitReason,
            eventType,
            slotStatus.ToString());
        if (string.Equals(_priorJournalTerminalMirrorCompletedKey, mirrorKey, StringComparison.Ordinal))
            return;

        if (TryMirrorCarryForwardTerminalCommitToPriorJournal(utcNow, commitReason, eventType, slotStatus))
            _priorJournalTerminalMirrorCompletedKey = mirrorKey;
    }

    private bool TryMirrorCarryForwardTerminalCommitToPriorJournal(
        DateTimeOffset utcNow,
        string commitReason,
        string eventType,
        SlotStatus slotStatus)
    {
        if (string.IsNullOrWhiteSpace(_journal.PriorJournalKey))
            return true;
        if (!TryParseStreamJournalKey(_journal.PriorJournalKey, out var priorTradingDate, out var priorStream))
            return true;
        if (string.Equals(priorTradingDate, TradingDate, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(priorStream, Stream, StringComparison.OrdinalIgnoreCase))
            return true;

        StreamJournal? prior;
        try
        {
            prior = _journals.TryLoad(priorTradingDate, priorStream);
        }
        catch
        {
            return false;
        }

        if (prior == null)
            return false;
        var sameCarryForwardLifecycle = IsSameCarryForwardLifecycle(prior);
        var referencedByPriorKey = IsJournalReferencedByCurrentPriorKey(prior);
        if (prior.Committed || (!sameCarryForwardLifecycle && !referencedByPriorKey))
            return true;

        var priorCommitAuthority = EvaluateTerminalCommitAuthorityForJournal(
            utcNow,
            commitReason,
            eventType,
            prior,
            priorTradingDate,
            priorStream,
            "StreamStateMachine.Commit.CarryForwardPriorJournal",
            forceCompletedTradeForStream: referencedByPriorKey && _journal.Committed,
            forceOpenLifecycleExposureOrPendingReentry: referencedByPriorKey && _journal.Committed ? false : null);
        if (!priorCommitAuthority.Allowed)
        {
            _log.Write(RobotEvents.Base(_time, utcNow, priorTradingDate, priorStream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "CARRY_FORWARD_PRIOR_JOURNAL_TERMINALIZE_DENIED_BY_AUTHORITY", prior.LastState ?? "",
                new
                {
                    commit_reason = commitReason,
                    event_type = eventType,
                    current_trading_date = TradingDate,
                    current_stream = Stream,
                    prior_journal_key = _journal.PriorJournalKey ?? "",
                    reentry_intent_id = prior.ReentryIntentId ?? "",
                    original_intent_id = prior.OriginalIntentId ?? "",
                    authority_gate = priorCommitAuthority.GateName,
                    authority_deny_reason = priorCommitAuthority.DenyReason ?? "",
                    note = "Carry-forward prior journal was not terminalized because canonical authority denied terminal commit for the prior journal evidence."
                }));
            return false;
        }

        prior.Committed = true;
        prior.CommitReason = commitReason;
        prior.EntryDetected = _journal.EntryDetected;
        prior.LastState = StreamState.DONE.ToString();
        prior.LastUpdateUtc = utcNow.ToString("o");
        prior.TimetableHashAtCommit ??= _journal.TimetableHashAtCommit ?? _timetableHash;
        prior.TerminalState = _journal.TerminalState;
        prior.SlotStatus = slotStatus;
        prior.ExecutionInterruptedByClose = _journal.ExecutionInterruptedByClose;
        prior.ReentryIntentId = _journal.ReentryIntentId;
        prior.ReentrySubmitPending = _journal.ReentrySubmitPending;
        prior.ReentrySubmitted = _journal.ReentrySubmitted;
        prior.ReentryFilled = _journal.ReentryFilled;
        prior.ProtectionSubmitted = _journal.ProtectionSubmitted;
        prior.ProtectionAccepted = _journal.ProtectionAccepted;

        var persisted = _journals.Save(prior);
        _log.Write(RobotEvents.Base(_time, utcNow, priorTradingDate, priorStream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "CARRY_FORWARD_PRIOR_JOURNAL_TERMINALIZED", StreamState.DONE.ToString(),
            new
            {
                committed = persisted,
                commit_reason = commitReason,
                event_type = eventType,
                current_trading_date = TradingDate,
                current_stream = Stream,
                slot_instance_key = _journal.SlotInstanceKey ?? "",
                prior_journal_key = _journal.PriorJournalKey ?? "",
                reentry_intent_id = _journal.ReentryIntentId ?? "",
                original_intent_id = _journal.OriginalIntentId ?? "",
                note = "Carry-forward lifecycle terminal commit was mirrored to the original prior-day journal so shutdown summary does not retain stale active streams."
            }));
        return persisted;
    }

    private static bool TryParseStreamJournalKey(string key, out string tradingDate, out string stream)
    {
        tradingDate = "";
        stream = "";
        var parts = key.Split('_');
        if (parts.Length < 2)
            return false;
        tradingDate = parts[0];
        stream = string.Join("_", parts.Skip(1));
        return !string.IsNullOrWhiteSpace(tradingDate) && !string.IsNullOrWhiteSpace(stream);
    }

    private bool IsSameCarryForwardLifecycle(StreamJournal prior)
    {
        if (!string.IsNullOrWhiteSpace(prior.SlotInstanceKey) &&
            !string.IsNullOrWhiteSpace(_journal.SlotInstanceKey) &&
            string.Equals(prior.SlotInstanceKey, _journal.SlotInstanceKey, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(prior.ReentryIntentId) &&
            !string.IsNullOrWhiteSpace(_journal.ReentryIntentId) &&
            string.Equals(prior.ReentryIntentId, _journal.ReentryIntentId, StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(prior.OriginalIntentId) &&
               !string.IsNullOrWhiteSpace(_journal.OriginalIntentId) &&
               string.Equals(prior.OriginalIntentId, _journal.OriginalIntentId, StringComparison.OrdinalIgnoreCase);
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
