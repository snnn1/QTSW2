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
    /// Check for market open re-entry (time-based, not bar-based).
    /// Evaluated in Tick() - trigger = now >= session_open AND market live (tick/price observed).
    /// </summary>
    private void CheckMarketOpenReentry(DateTimeOffset utcNow)
    {
        if (_journal.SlotStatus != SlotStatus.ACTIVE || 
            !_journal.ExecutionInterruptedByClose || 
            _journal.ReentrySubmitted ||
            string.IsNullOrWhiteSpace(_journal.OriginalIntentId) ||
            _executionJournal == null ||
            _executionAdapter == null)
        {
            return; // Conditions not met for re-entry
        }

        if (_journal.ReentrySubmitPending)
        {
            var pendingAgeSeconds = _journal.ReentrySubmitPendingAtUtc.HasValue
                ? (utcNow - _journal.ReentrySubmitPendingAtUtc.Value).TotalSeconds
                : 0.0;
            if (pendingAgeSeconds < 120.0)
                return;

            _journal.ReentrySubmitPending = false;
            _journal.ReentrySubmitPendingAtUtc = null;
            _journals.Save(_journal);
            LogSessionReentryBlocked(utcNow, "SUBMIT_PENDING_STALE_RETRY",
                "Prior reentry submit remained pending beyond timeout; clearing pending state and retrying");
        }

        if (_journal.ReentrySubmitLastFailureUtc.HasValue &&
            (utcNow - _journal.ReentrySubmitLastFailureUtc.Value).TotalSeconds < 30.0)
        {
            return;
        }
        
        // Time gate: utcNow >= ReentryAllowedUtc (from SessionCloseResolver or spec fallback).
        // No reentry when HasSession=false (holiday).
        if (_engine != null)
        {
            var (reentryUtc, hasSession) = _engine.GetReentryAllowedUtc(TradingDate, Session, utcNow);
            if (!hasSession)
                return; // Holiday - no session, no reentry
            if (reentryUtc.HasValue && utcNow < reentryUtc.Value)
                return; // Before market reopen
            if (!reentryUtc.HasValue)
            {
                var nowChicago = _time.ConvertUtcToChicago(utcNow);
                if (nowChicago < MarketReopenChicagoTime)
                    return;
            }
        }
        else
        {
            // No engine (e.g. harness) - use spec fallback
            var nowChicago = _time.ConvertUtcToChicago(utcNow);
            if (nowChicago < MarketReopenChicagoTime)
                return;
        }
        
        // Market tradeable signal: At least one tick/price observed since reopen
        // For now, assume market is live if we're past session open (can be enhanced with actual tick observation)
        // TODO: Add explicit tick observation tracking for more precise market-open detection
        
        // Check slot not expired
        if (_journal.NextSlotTimeUtc.HasValue && utcNow >= _journal.NextSlotTimeUtc.Value)
        {
            return; // Slot expired, no re-entry
        }
        
        // Block checks: instrument blocked or IEA queue unhealthy - do not enqueue reentry
        var execInst = ExecutionInstrument ?? Instrument ?? "";
        if (!string.IsNullOrEmpty(execInst))
        {
            if (_isInstrumentBlockedForReentry?.Invoke(execInst) == true)
            {
                LogSessionReentryBlocked(utcNow, "INSTRUMENT_BLOCKED",
                    "Instrument blocked or frozen for reentry (reconciliation / flatten latch)");
                return;
            }
            if (_isIeaQueueHealthyForInstrument?.Invoke(execInst) == false)
            {
                LogSessionReentryBlocked(utcNow, "IEA_QUEUE_UNHEALTHY",
                    "IE execution adapter queue unhealthy or poison — reentry not allowed");
                return;
            }
        }

        try
        {
            var snap = _executionAdapter.GetAccountSnapshot(utcNow);
            var exposure = BrokerPositionResolver.ResolveFromSnapshots(snap.Positions ?? new List<PositionSnapshot>(), execInst);
            if (exposure.ReconciliationAbsQuantityTotal != 0)
            {
                LogSessionReentryBlocked(utcNow, "BROKER_NOT_FLAT",
                    "Broker exposure is not flat at market-open reentry preflight",
                    new Dictionary<string, object?>
                    {
                        ["instrument"] = execInst,
                        ["canonical_key"] = exposure.CanonicalKey,
                        ["broker_abs_quantity"] = exposure.ReconciliationAbsQuantityTotal,
                        ["broker_legs"] = BrokerPositionResolver.ToDiagnosticRows(exposure)
                    });
                return;
            }
        }
        catch (Exception ex)
        {
            LogSessionReentryBlocked(utcNow, "BROKER_FLAT_PREFLIGHT_ERROR",
                "Unable to verify broker flat before market-open reentry",
                new Dictionary<string, object?>
                {
                    ["instrument"] = execInst,
                    ["error"] = ex.Message,
                    ["exception_type"] = ex.GetType().Name
                });
            return;
        }
        
        // Load bracket levels from ExecutionJournalEntry via OriginalIntentId (canonical source)
        // CRITICAL: OriginalIntentId was stored from previous trading date, so search across all dates
        // Use PriorJournalKey to get original TradingDate if available, otherwise search all dates
        string? originalTradingDate = null;
        if (!string.IsNullOrWhiteSpace(_journal.PriorJournalKey))
        {
            // PriorJournalKey format: "{PreviousTradingDate}_{Stream}"
            var parts = _journal.PriorJournalKey.Split('_');
            if (parts.Length >= 1)
            {
                originalTradingDate = parts[0];
            }
        }
        
        var pattern = originalTradingDate != null
            ? $"{originalTradingDate}_*_{_journal.OriginalIntentId}.json"
            : $"*_*_{_journal.OriginalIntentId}.json"; // Search all dates if PriorJournalKey not available
        var journalDir = RobotRunArtifactPaths.StateExecutionJournals(_robotStateRoot);
        ExecutionJournalEntry? originalEntry = null;
        
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
                        if (entry != null && entry.IntentId == _journal.OriginalIntentId)
                        {
                            originalEntry = entry;
                            break;
                        }
                    }
                    catch
                    {
                        // Skip corrupted files
                    }
                }
            }
        }
        catch
        {
            // Fail-safe: cannot load entry
        }
        
        if (originalEntry == null || !originalEntry.EntryFilled)
        {
            // Cannot load original entry or entry not filled - fail closed
            _ = Commit(utcNow, "REENTRY_FAILED_CANNOT_LOAD_ORIGINAL_ENTRY", "REENTRY_FAILED");
            LogSessionReentryBlocked(utcNow, "CANNOT_LOAD_ORIGINAL_ENTRY",
                $"Cannot load original ExecutionJournalEntry for intent {_journal.OriginalIntentId}");
            LogHealth("CRITICAL", "REENTRY_FAILED", $"Re-entry failed: Cannot load original ExecutionJournalEntry for intent {_journal.OriginalIntentId}",
                new { original_intent_id = _journal.OriginalIntentId });
            return;
        }
        
        // Pre-re-entry safety check: Verify stop price validity and protection placement conditions
        // For now, assume safety checks pass (can be enhanced with actual price gap detection)
        // TODO: Add explicit gap detection and price validity checks

        var direction = originalEntry.Direction ?? "Long";
        var quantity = originalEntry.EntryFilledQuantityTotal;
        var reentryEntryPrice = originalEntry.EntryPrice;
        var reentryStopPrice = originalEntry.StopPrice;
        var reentryTargetPrice = originalEntry.TargetPrice;
        var reentryBeTrigger = originalEntry.BETriggerPrice;

        if (quantity <= 0)
        {
            // Invalid quantity - fail closed
            _ = Commit(utcNow, "REENTRY_FAILED_INVALID_QUANTITY", "REENTRY_FAILED");
            LogSessionReentryBlocked(utcNow, "INVALID_QUANTITY", $"Re-entry failed: invalid quantity {quantity}");
            LogHealth("CRITICAL", "REENTRY_FAILED", $"Re-entry failed: Invalid quantity {quantity}",
                new { original_intent_id = _journal.OriginalIntentId, quantity });
            return;
        }

        // Canonical reentry intent id — must match NinjaTraderSimAdapter.ExecuteSubmitMarketReentry Intent construction
        // (same strings: F2/NULL rules in ExecutionJournal.ComputeIntentId; slot/session verbatim; direction defaults to Long when empty in NT).
        var instrumentRaw = ExecutionInstrument ?? Instrument ?? "";
        var execInstUpper = string.IsNullOrEmpty(instrumentRaw) ? "" : instrumentRaw.Trim().ToUpperInvariant();
        var reentryIntentForCanonicalId = new Intent(
            TradingDate ?? "",
            Stream,
            instrumentRaw,
            execInstUpper,
            Session,
            SlotTimeChicago,
            direction,
            reentryEntryPrice,
            reentryStopPrice,
            reentryTargetPrice,
            reentryBeTrigger,
            utcNow,
            "SUBMIT_MARKET_REENTRY");
        _journal.ReentryIntentId = reentryIntentForCanonicalId.ComputeIntentId();

        _engine?.LogEngineEvent(utcNow, "SESSION_REENTRY_ATTEMPT", new Dictionary<string, object?>
        {
            ["trading_date"] = TradingDate,
            ["session_class"] = Session,
            ["instrument"] = ExecutionInstrument ?? Instrument ?? "",
            ["stream"] = Stream,
            ["original_intent_id"] = _journal.OriginalIntentId ?? "",
            ["reentry_intent_id"] = _journal.ReentryIntentId ?? "",
            ["reason"] = "MARKET_OPEN_GATES_PASSED"
        });
        
        // Enqueue SubmitMarketReentryCommand through IEA for actual order placement
        var cmd = new SubmitMarketReentryCommand
        {
            CommandId = Guid.NewGuid().ToString(),
            TimestampUtc = utcNow,
            Instrument = ExecutionInstrument ?? Instrument ?? "",
            IntentId = _journal.ReentryIntentId,
            Stream = Stream,
            Session = Session,
            SlotTimeChicago = SlotTimeChicago,
            TradingDate = TradingDate,
            ExecutionInstrument = ExecutionInstrument ?? Instrument ?? "",
            ReentryIntentId = _journal.ReentryIntentId,
            OriginalIntentId = _journal.OriginalIntentId,
            Direction = direction,
            EntryPrice = reentryEntryPrice,
            StopPrice = reentryStopPrice,
            TargetPrice = reentryTargetPrice,
            BeTrigger = reentryBeTrigger,
            Quantity = (int)quantity,
            Reason = "MARKET_REENTRY",
            CallerContext = "CheckMarketOpenReentry"
        };

        _journal.ReentrySubmitPending = true;
        _journal.ReentrySubmitPendingAtUtc = utcNow;
        _journals.Save(_journal);

        _executionAdapter.EnqueueExecutionCommand(cmd);

        _engine?.LogEngineEvent(utcNow, "SESSION_REENTRY_EXECUTED", new Dictionary<string, object?>
        {
            ["trading_date"] = TradingDate,
            ["session_class"] = Session,
            ["instrument"] = ExecutionInstrument ?? Instrument ?? "",
            ["stream"] = Stream,
            ["original_intent_id"] = _journal.OriginalIntentId ?? "",
            ["reentry_intent_id"] = _journal.ReentryIntentId ?? "",
            ["command_id"] = cmd.CommandId,
            ["reason"] = "REENTRY_COMMAND_ENQUEUED"
        });
        
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "REENTRY_SUBMIT_ENQUEUED", State.ToString(),
            new
            {
                reentry_intent_id = _journal.ReentryIntentId,
                original_intent_id = _journal.OriginalIntentId,
                direction = direction ?? "NULL",
                quantity = quantity,
                stop_price = originalEntry.StopPrice,
                target_price = originalEntry.TargetPrice,
                entry_price = originalEntry.EntryPrice,
                command_id = cmd.CommandId,
                note = "Re-entry MARKET order enqueued via IEA; awaiting broker submit completion"
            }));
    }

    public void HandleLateSessionCloseFlattenConfirmed(DateTimeOffset utcNow)
    {
        if (!IsActiveInterruptedBySessionClose || string.IsNullOrWhiteSpace(_journal.OriginalIntentId))
            return;

        var reentryEvaluationUtc = utcNow;
        var reentryEvaluationSource = "late_confirm_callback";
        if (_journal.NextSlotTimeUtc.HasValue && reentryEvaluationUtc >= _journal.NextSlotTimeUtc.Value)
        {
            // Playback order callbacks can carry wall-clock time long after the historical slot. Do not
            // synthesize a future in-slot timestamp, because that bypasses the market-open gate and
            // re-enters immediately after flatten. Fall back to the logical forced-flatten time and let
            // ordinary replay ticks submit only when the configured reopen time is actually reached.
            if (_journal.ForcedFlattenTimestamp.HasValue)
            {
                reentryEvaluationUtc = _journal.ForcedFlattenTimestamp.Value;
                reentryEvaluationSource = "forced_flatten_timestamp_after_wall_clock_callback";
            }
            else
            {
                reentryEvaluationSource = "late_confirm_callback_after_slot_no_logical_fallback";
            }
        }

        _journal.LastUpdateUtc = utcNow.ToString("o");
        _journals.Save(_journal);

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "SESSION_CLOSE_FLATTEN_LATE_CONFIRMED_REENTRY_READY", State.ToString(),
            new
            {
                original_intent_id = _journal.OriginalIntentId ?? "",
                execution_instrument = ExecutionInstrument ?? Instrument ?? "",
                forced_flatten_timestamp_utc = _journal.ForcedFlattenTimestamp?.ToString("o") ?? "",
                late_confirm_wall_clock_utc = utcNow.ToString("o"),
                reentry_evaluation_utc = reentryEvaluationUtc.ToString("o"),
                reentry_evaluation_source = reentryEvaluationSource,
                next_slot_time_utc = _journal.NextSlotTimeUtc?.ToString("o") ?? "",
                reentry_submitted = _journal.ReentrySubmitted,
                note = "Late forced-flatten fill closed the original trade and broker is flat; stream remains active for market-open reentry."
            }));

        // Late broker-flat proof can arrive after the last ordinary tick for the interrupted stream.
        // Kick the normal reentry gate immediately so the stream does not stay stranded waiting for
        // another tick that may never arrive before shutdown.
        CheckMarketOpenReentry(reentryEvaluationUtc);
    }

    /// <summary>
    /// Handle strategy-thread completion of re-entry market order submission.
    /// </summary>
    public void HandleReentrySubmitCompleted(string reentryIntentId, DateTimeOffset utcNow, bool success, string? error)
    {
        if (_journal.ReentryIntentId != reentryIntentId)
            return;

        _journal.ReentrySubmitPending = false;
        _journal.ReentrySubmitPendingAtUtc = null;

        if (success)
        {
            _journal.ReentrySubmitted = true;
            _journal.ReentrySubmitFailureCount = 0;
            _journal.ReentrySubmitLastFailureUtc = null;
            _journal.LastReentrySubmitError = null;
            _journals.Save(_journal);

            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "REENTRY_SUBMITTED", State.ToString(),
                new
                {
                    reentry_intent_id = reentryIntentId,
                    original_intent_id = _journal.OriginalIntentId,
                    note = "Re-entry MARKET order accepted by execution adapter"
                }));
            return;
        }

        _journal.ReentrySubmitted = false;
        _journal.ReentrySubmitFailureCount++;
        _journal.ReentrySubmitLastFailureUtc = utcNow;
        _journal.LastReentrySubmitError = error ?? "";
        _journals.Save(_journal);

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "REENTRY_SUBMIT_FAILED", State.ToString(),
            new
            {
                reentry_intent_id = reentryIntentId,
                original_intent_id = _journal.OriginalIntentId,
                failure_count = _journal.ReentrySubmitFailureCount,
                error = error ?? "",
                transient_retry = IsTransientReentrySubmitFailure(error)
            }));

        if (IsTransientReentrySubmitFailure(error))
        {
            LogSessionReentryBlocked(utcNow, "REENTRY_SUBMIT_TRANSIENT_UNSAFE_STATE",
                "Re-entry submit blocked by transient execution safety state; slot will remain active and retry",
                new Dictionary<string, object?>
                {
                    ["reentry_intent_id"] = reentryIntentId,
                    ["error"] = error ?? "",
                    ["failure_count"] = _journal.ReentrySubmitFailureCount
                });

            if (_journal.ReentrySubmitFailureCount >= 60)
            {
                _ = Commit(utcNow, "REENTRY_FAILED_TRANSIENT_SAFETY_TIMEOUT", "REENTRY_FAILED");
                LogHealth("CRITICAL", "REENTRY_FAILED", "Re-entry market submit stayed blocked by transient safety state for too long; slot stood down",
                    new { reentry_intent_id = reentryIntentId, error = error ?? "", failure_count = _journal.ReentrySubmitFailureCount });
            }
            return;
        }

        if (_journal.ReentrySubmitFailureCount >= 3)
        {
            _ = Commit(utcNow, "REENTRY_FAILED_SUBMIT_REJECTED", "REENTRY_FAILED");
            LogHealth("CRITICAL", "REENTRY_FAILED", "Re-entry market submit failed repeatedly; slot stood down",
                new { reentry_intent_id = reentryIntentId, error = error ?? "" });
        }
    }

    private static bool IsTransientReentrySubmitFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;

        var normalized = error.Trim();
        if (normalized.StartsWith("EXECUTION_BLOCKED_UNSAFE_STATE:", StringComparison.Ordinal))
            return true;
        if (normalized.StartsWith("EXECUTION_BLOCKED_EPA:MISMATCH_EXECUTION_BLOCK", StringComparison.Ordinal))
            return true;
        if (normalized.StartsWith("UEA_DENIED:", StringComparison.Ordinal) &&
            (normalized.IndexOf("MISMATCH", StringComparison.OrdinalIgnoreCase) >= 0 ||
             normalized.IndexOf("RECOVERY", StringComparison.OrdinalIgnoreCase) >= 0 ||
             normalized.IndexOf("UNSAFE", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return true;
        }

        return normalized.IndexOf("quant_recovery_required", StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.IndexOf("repair_active", StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.IndexOf("state_consistency", StringComparison.OrdinalIgnoreCase) >= 0;
    }
    
    /// <summary>
    /// Handle re-entry fill (called by execution adapter when re-entry order fills).
    /// </summary>
    public void HandleReentryFill(string reentryIntentId, DateTimeOffset utcNow)
    {
        if (_journal.ReentryIntentId != reentryIntentId || _journal.ReentryFilled)
        {
            return; // Not our re-entry or already filled
        }
        
        _journal.ReentryFilled = true;
        _journals.Save(_journal);
        
        // Load bracket levels from OriginalIntentId
        if (string.IsNullOrWhiteSpace(_journal.OriginalIntentId) || _executionJournal == null)
        {
            // Cannot load bracket levels - fail closed
            _ = Commit(utcNow, "REENTRY_PROTECTION_FAILED_CANNOT_LOAD_BRACKET", "REENTRY_PROTECTION_FAILED");
            LogHealth("CRITICAL", "REENTRY_PROTECTION_FAILED", $"Re-entry protection failed: Cannot load bracket levels for intent {_journal.OriginalIntentId}",
                new { original_intent_id = _journal.OriginalIntentId });
            return;
        }
        
        // Resolve original trading date and stream from PriorJournalKey
        string? originalTradingDate = null;
        string? originalStream = null;
        if (!string.IsNullOrWhiteSpace(_journal.PriorJournalKey))
        {
            var parts = _journal.PriorJournalKey.Split('_');
            if (parts.Length >= 1) originalTradingDate = parts[0];
            if (parts.Length >= 2) originalStream = string.Join("_", parts.Skip(1));
        }
        if (string.IsNullOrEmpty(originalTradingDate)) originalTradingDate = TradingDate;
        if (string.IsNullOrEmpty(originalStream)) originalStream = Stream;
        
        var originalEntry = _executionJournal.GetEntry(_journal.OriginalIntentId, originalTradingDate, originalStream);
        if (originalEntry == null || !originalEntry.StopPrice.HasValue || !originalEntry.TargetPrice.HasValue)
        {
            _ = Commit(utcNow, "REENTRY_PROTECTION_FAILED_CANNOT_LOAD_BRACKET", "REENTRY_PROTECTION_FAILED");
            LogHealth("CRITICAL", "REENTRY_PROTECTION_FAILED", $"Re-entry protection failed: Cannot load bracket levels for intent {_journal.OriginalIntentId}",
                new { original_intent_id = _journal.OriginalIntentId });
            return;
        }
        
        var stopPrice = originalEntry.StopPrice.Value;
        var targetPrice = originalEntry.TargetPrice.Value;
        var direction = originalEntry.Direction ?? "Long";
        var quantity = originalEntry.EntryFilledQuantityTotal;
        if (quantity <= 0) quantity = 1;
        
        // Submit protective bracket via execution adapter
        if (_executionAdapter != null)
        {
            var stopResult = _executionAdapter.SubmitProtectiveStop(reentryIntentId, ExecutionInstrument ?? Instrument, direction, stopPrice, quantity, null, utcNow);
            var targetResult = _executionAdapter.SubmitTargetOrder(reentryIntentId, ExecutionInstrument ?? Instrument, direction, targetPrice, quantity, null, utcNow);
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "REENTRY_PROTECTIVES_SUBMITTED", State.ToString(),
                new
                {
                    reentry_intent_id = reentryIntentId,
                    stop_success = stopResult.Success,
                    target_success = targetResult.Success,
                    stop_price = stopPrice,
                    target_price = targetPrice
                }));

            if (stopResult.Success && targetResult.Success)
            {
                _journal.ProtectionSubmitted = true;
                _journals.Save(_journal);
            }
            else
            {
                _journal.ProtectionSubmitted = false;
                _journals.Save(_journal);
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "REENTRY_PROTECTION_SUBMIT_FAILED", State.ToString(),
                    new
                    {
                        reentry_intent_id = reentryIntentId,
                        stop_success = stopResult.Success,
                        stop_error = stopResult.ErrorMessage,
                        target_success = targetResult.Success,
                        target_error = targetResult.ErrorMessage,
                        instrument = ExecutionInstrument ?? Instrument ?? ""
                    }));
                var flattenQueued = _executionAdapter.TryEnqueueEmergencyFlattenProtective(ExecutionInstrument ?? Instrument ?? "", utcNow);
                _ = Commit(utcNow, "REENTRY_PROTECTION_FAILED_SUBMIT_REJECTED", "REENTRY_PROTECTION_FAILED");
                LogHealth("CRITICAL", "REENTRY_PROTECTION_FAILED", "Re-entry filled but protective submit failed; emergency flatten requested",
                    new { reentry_intent_id = reentryIntentId, flatten_queued = flattenQueued });
                return;
            }
        }
        else
        {
            _ = Commit(utcNow, "REENTRY_PROTECTION_FAILED_NO_ADAPTER", "REENTRY_PROTECTION_FAILED");
            LogHealth("CRITICAL", "REENTRY_PROTECTION_FAILED", "Re-entry filled but no execution adapter was available for protectives",
                new { reentry_intent_id = reentryIntentId });
            return;
        }
        
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "REENTRY_FILLED", State.ToString(),
            new
            {
                reentry_intent_id = reentryIntentId,
                original_intent_id = _journal.OriginalIntentId,
                note = "Re-entry filled - protective bracket submitted"
            }));
    }
    
    /// <summary>
    /// Handle re-entry protection acceptance (called by execution adapter when protective orders accepted).
    /// </summary>
    /// <param name="utcNow">Current UTC time.</param>
    /// <param name="reentryIntentId">Reentry intent ID that had protectives accepted. If non-null, must match _journal.ReentryIntentId.</param>
    public void HandleReentryProtectionAccepted(DateTimeOffset utcNow, string? reentryIntentId = null)
    {
        if (!_journal.ProtectionSubmitted)
        {
            return; // Protection not submitted (ensures we only process reentry, not original entry)
        }
        if (!string.IsNullOrEmpty(reentryIntentId) && _journal.ReentryIntentId != reentryIntentId)
        {
            return; // Not our reentry intent
        }
        
        _journal.ProtectionAccepted = true;
        _journal.ExecutionInterruptedByClose = false; // Clear interruption flag after protection confirmed
        _journals.Save(_journal);
        
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "REENTRY_PROTECTION_ACCEPTED", State.ToString(),
            new
            {
                reentry_intent_id = _journal.ReentryIntentId,
                original_intent_id = _journal.OriginalIntentId,
                note = "Re-entry protection accepted - slot resumed normal operation"
            }));
    }

    /// <summary>
    /// Close the stream journal when the reentry execution journal reaches terminal completion.
    /// </summary>
    public void HandleExecutionTradeCompleted(string intentId, DateTimeOffset utcNow, string? completionReason = null)
    {
        if (_journal.Committed || string.IsNullOrWhiteSpace(intentId))
            return;

        if (!string.Equals(_journal.ReentryIntentId, intentId, StringComparison.OrdinalIgnoreCase))
            return;

        _journal.ReentryFilled = true;
        _journal.ExecutionInterruptedByClose = false;
        _journals.Save(_journal);

        var reason = string.Equals(completionReason, CompletionReasons.RECONCILIATION_BROKER_FLAT, StringComparison.OrdinalIgnoreCase)
            ? "REENTRY_TRADE_COMPLETED_RECONCILIATION_BROKER_FLAT"
            : "REENTRY_TRADE_COMPLETED";

        _ = Commit(utcNow, reason, "TRADE_COMPLETED");
    }
}
